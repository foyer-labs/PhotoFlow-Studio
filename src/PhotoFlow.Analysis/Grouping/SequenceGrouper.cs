// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Analysis.Grouping;

/// <summary>A photo as seen by the grouping: capture time from EXIF (null when unknown), camera, exposure, embedding.</summary>
/// <param name="Order">Position in the job (file order), used when there is no capture time.</param>
/// <param name="ExposureValue">EV at ISO 100 (log2(N²/t) − log2(ISO/100)) when the EXIF has it: manual bracketing.</param>
public sealed record GroupingPhoto(
    long AssetId,
    int Order,
    DateTimeOffset? CapturedAt,
    string? Camera,
    double? ExposureCompensationEv,
    double? ExposureValue,
    float[]? Embedding);

/// <summary>
/// Bursts and special sequences of spec §6, decided per camera on the shots ordered by capture time:
/// <list type="number">
/// <item><b>Timelapse</b>: at least <see cref="TimelapseMinShots"/> shots at a regular interval of at least
/// <see cref="TimelapseMinIntervalSeconds"/> (each interval within ±10% of the first, or ±1 s when the times have no
/// sub-seconds). Never ranked.</item>
/// <item><b>Bracketing</b>: shots within the burst gap whose exposure compensation varies (or whose exposure spans at
/// least <see cref="BracketMinExposureSpan"/> EV in manual mode). Never ranked, never "similar".</item>
/// <item><b>Burst</b>: the remaining shots within the gap of the profile (P17, 2.0 s), split where the similarity of
/// two consecutive embeddings drops below the threshold (0.90: change of subject). Photos without capture time: only
/// similarity, in file order. Without embeddings (CLIP not installed) time alone decides.</item>
/// </list>
/// Groups have at least two photos; a photo belongs to one group.
/// </summary>
public static class SequenceGrouper
{
    public const int TimelapseMinShots = 10;
    public const double TimelapseMinIntervalSeconds = 1.0;
    public const double TimelapseTolerance = 0.10;
    public const double BracketMinCompensationStep = 0.3;
    public const double BracketMinExposureSpan = 1.0;

    /// <summary>Capture times are rounded (milliseconds or less): intervals are compared with this slack.</summary>
    private const double TimeSlack = 0.001;

    public static IReadOnlyList<(GroupType Type, IReadOnlyList<long> AssetIds)> Group(IReadOnlyList<GroupingPhoto> photos, GroupingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(parameters);
        var groups = new List<(GroupType, IReadOnlyList<long>)>();

        foreach (var camera in photos.Where(p => p.CapturedAt is not null).GroupBy(p => p.Camera ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var shots = camera.OrderBy(p => p.CapturedAt).ThenBy(p => p.Order).ToList();
            var subSeconds = shots.Any(p => p.CapturedAt!.Value.Ticks % TimeSpan.TicksPerSecond != 0);
            var used = new bool[shots.Count];

            foreach (var (start, end) in Timelapses(shots, subSeconds))
            {
                groups.Add((GroupType.Timelapse, shots[start..(end + 1)].Select(p => p.AssetId).ToList()));
                for (var i = start; i <= end; i++)
                {
                    used[i] = true;
                }
            }

            // Runs of consecutive shots (not in a timelapse) within the burst gap.
            var run = new List<GroupingPhoto>();
            for (var i = 0; i <= shots.Count; i++)
            {
                var continues = i < shots.Count && !used[i] && run.Count > 0
                                && (shots[i].CapturedAt!.Value - run[^1].CapturedAt!.Value).TotalSeconds <= parameters.MaxBurstGapSeconds + TimeSlack;
                if (!continues)
                {
                    Close(run, parameters, groups);
                    run = [];
                }

                if (i < shots.Count && !used[i])
                {
                    run.Add(shots[i]);
                }
            }
        }

        // Without capture time: similarity only, in file order.
        var untimed = photos.Where(p => p.CapturedAt is null).OrderBy(p => p.Order).ToList();
        foreach (var segment in SplitBySimilarity(untimed, parameters.MinSimilarity, requireEmbeddings: true))
        {
            if (segment.Count >= 2)
            {
                groups.Add((GroupType.Burst, segment.Select(p => p.AssetId).ToList()));
            }
        }

        return groups;
    }

    private static void Close(List<GroupingPhoto> run, GroupingParameters parameters, List<(GroupType, IReadOnlyList<long>)> groups)
    {
        if (run.Count < 2)
        {
            return;
        }

        if (IsBracketing(run))
        {
            groups.Add((GroupType.Bracketing, run.Select(p => p.AssetId).ToList()));
            return;
        }

        foreach (var segment in SplitBySimilarity(run, parameters.MinSimilarity, requireEmbeddings: false))
        {
            if (segment.Count >= 2)
            {
                groups.Add((GroupType.Burst, segment.Select(p => p.AssetId).ToList()));
            }
        }
    }

    /// <summary>Exposure compensation that changes within the run, or (manual bracketing) a wide exposure span.</summary>
    public static bool IsBracketing(IReadOnlyList<GroupingPhoto> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var compensations = run.Where(p => p.ExposureCompensationEv is not null).Select(p => p.ExposureCompensationEv!.Value).ToList();
        if (compensations.Count >= 2 && compensations.Max() - compensations.Min() >= BracketMinCompensationStep)
        {
            return true;
        }

        var exposures = run.Where(p => p.ExposureValue is not null).Select(p => p.ExposureValue!.Value).ToList();
        return exposures.Count >= 3 && exposures.Count == run.Count && exposures.Max() - exposures.Min() >= BracketMinExposureSpan;
    }

    /// <summary>Maximal runs of regular intervals (start and end index, inclusive).</summary>
    private static IEnumerable<(int Start, int End)> Timelapses(IReadOnlyList<GroupingPhoto> shots, bool subSeconds)
    {
        var i = 0;
        while (i < shots.Count - 1)
        {
            var reference = Interval(shots, i);
            var end = i;
            if (reference >= TimelapseMinIntervalSeconds - TimeSlack)
            {
                var tolerance = subSeconds ? Math.Max(TimelapseTolerance * reference, 0.15) : 1.0;
                end = i + 1;
                while (end < shots.Count - 1 && Interval(shots, end) >= TimelapseMinIntervalSeconds - TimeSlack && Math.Abs(Interval(shots, end) - reference) <= tolerance + TimeSlack)
                {
                    end++;
                }
            }

            if (end - i + 1 >= TimelapseMinShots)
            {
                yield return (i, end);
                i = end + 1;
            }
            else
            {
                i++;
            }
        }
    }

    private static double Interval(IReadOnlyList<GroupingPhoto> shots, int i) =>
        (shots[i + 1].CapturedAt!.Value - shots[i].CapturedAt!.Value).TotalSeconds;

    /// <summary>Splits where two consecutive embeddings are less similar than the threshold (change of subject).</summary>
    private static List<List<GroupingPhoto>> SplitBySimilarity(IReadOnlyList<GroupingPhoto> photos, double minSimilarity, bool requireEmbeddings)
    {
        var segments = new List<List<GroupingPhoto>>();
        var current = new List<GroupingPhoto>();
        foreach (var photo in photos)
        {
            if (current.Count > 0)
            {
                var previous = current[^1];
                var similar = previous.Embedding is { } a && photo.Embedding is { } b
                    ? Vectors.Cosine(a, b) >= minSimilarity
                    : !requireEmbeddings;
                if (!similar)
                {
                    segments.Add(current);
                    current = [];
                }
            }

            current.Add(photo);
        }

        if (current.Count > 0)
        {
            segments.Add(current);
        }

        return segments;
    }
}
