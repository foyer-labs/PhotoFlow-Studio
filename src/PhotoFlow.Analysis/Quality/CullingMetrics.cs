// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Analysis.Quality;

/// <summary>A proportion with its 95 % Wilson score interval.</summary>
public sealed record Proportion(int Successes, int Total)
{
    public const double Z95 = 1.959963984540054;

    public double Value => Total == 0 ? 0 : (double)Successes / Total;

    public (double Low, double High) Interval => Wilson(Successes, Total);

    /// <summary>Wilson score interval (better than the normal approximation for small samples and extreme rates).</summary>
    public static (double Low, double High) Wilson(int successes, int total, double z = Z95)
    {
        if (total == 0)
        {
            return (0, 1);
        }

        var p = (double)successes / total;
        var z2 = z * z;
        var denominator = 1 + (z2 / total);
        var centre = (p + (z2 / (2 * total))) / denominator;
        var margin = z * Math.Sqrt((p * (1 - p) / total) + (z2 / (4.0 * total * total))) / denominator;
        return (Math.Max(0, centre - margin), Math.Min(1, centre + margin));
    }
}

public enum MetricVerdict
{
    /// <summary>The whole interval meets the target.</summary>
    WithinTarget,

    /// <summary>The interval straddles the target.</summary>
    Uncertain,

    /// <summary>The whole interval misses the target.</summary>
    OffTarget,

    NoData,
}

public sealed record CullingMetric(string Key, Proportion Proportion, double Target, bool LowerIsBetter)
{
    public MetricVerdict Verdict
    {
        get
        {
            if (Proportion.Total == 0)
            {
                return MetricVerdict.NoData;
            }

            var (low, high) = Proportion.Interval;
            return LowerIsBetter
                ? high < Target ? MetricVerdict.WithinTarget : low >= Target ? MetricVerdict.OffTarget : MetricVerdict.Uncertain
                : low > Target ? MetricVerdict.WithinTarget : high <= Target ? MetricVerdict.OffTarget : MetricVerdict.Uncertain;
        }
    }
}

/// <summary>A labelled photo with the automatic verdict of the system and its profile.</summary>
public sealed record LabeledVerdict(PhotoLabel Label, CullingOutcome? Automatic, string ProfileId);

/// <summary>
/// The three culling metrics of spec §6 from the labelled set (spec §14, board 2d), each with its 95 % interval:
/// wrong discards (discards labelled good, target &lt; 2 %), intercepted discards (photos labelled blurred, eyes
/// closed or exposure that the system discards, target &gt; 70 %), share to review (target &lt; 20 %). The normal mode
/// unlocks only when all three intervals are within target (D23).
/// </summary>
public sealed record CullingMetrics(CullingMetric WrongDiscards, CullingMetric Intercepted, CullingMetric ToReview, IReadOnlyDictionary<string, int> PerProfile, int Labeled)
{
    public const double WrongDiscardsTarget = 0.02;
    public const double InterceptedTarget = 0.70;
    public const double ToReviewTarget = 0.20;

    public bool NormalModeAllowed =>
        WrongDiscards.Verdict == MetricVerdict.WithinTarget && Intercepted.Verdict == MetricVerdict.WithinTarget && ToReview.Verdict == MetricVerdict.WithinTarget;

    public static CullingMetrics Compute(IReadOnlyList<LabeledVerdict> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var measured = labels.Where(l => l.Label != PhotoLabel.BurstBest && l.Automatic is not null).ToList();
        var discards = measured.Where(l => l.Automatic == CullingOutcome.Discard).ToList();
        var bad = measured.Where(l => l.Label is PhotoLabel.Blurred or PhotoLabel.EyesClosed or PhotoLabel.Exposure).ToList();
        return new CullingMetrics(
            new("WrongDiscards", new Proportion(discards.Count(l => l.Label == PhotoLabel.Good), discards.Count), WrongDiscardsTarget, LowerIsBetter: true),
            new("Intercepted", new Proportion(bad.Count(l => l.Automatic == CullingOutcome.Discard), bad.Count), InterceptedTarget, LowerIsBetter: false),
            new("ToReview", new Proportion(measured.Count(l => l.Automatic == CullingOutcome.ToReview), measured.Count), ToReviewTarget, LowerIsBetter: true),
            measured.GroupBy(l => l.ProfileId).ToDictionary(g => g.Key, g => g.Count()),
            measured.Count);
    }
}
