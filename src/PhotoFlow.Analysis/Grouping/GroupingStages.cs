// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Culling;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Pipeline;

namespace PhotoFlow.Analysis.Grouping;

/// <summary>A photo of a job for the batch stages: its analyzed asset (the JPEG of a RAW+JPEG pair) and its twins.</summary>
public sealed record JobPhoto(long AnalyzedAssetId, IReadOnlyList<long> AssetIds, int Order, AnalyzerResultRecord Analysis);

public static class JobPhotos
{
    /// <summary>
    /// The photos of the job that finished the analysis, in job order: items grouped by folder and name (twins), the
    /// analyzed asset being the one with the culling result.
    /// </summary>
    public static async Task<IReadOnlyList<JobPhoto>> LoadAsync(ICatalog catalog, long jobId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var items = (await catalog.Jobs.GetItemsAsync(jobId, ct).ConfigureAwait(false))
            .Where(i => i.State == JobItemState.Done && i.AssetId is not null)
            .OrderBy(i => i.Id)
            .ToList();
        var results = await catalog.Analysis.GetResultsAsync(items.Select(i => i.AssetId!.Value).Distinct().ToList(), CullingAnalyzer.AnalyzerId, ct).ConfigureAwait(false);
        var photos = new List<JobPhoto>();
        foreach (var twins in items.GroupBy(i => Path.Combine(Path.GetDirectoryName(i.SourcePath) ?? string.Empty, Path.GetFileNameWithoutExtension(i.SourcePath)), StringComparer.OrdinalIgnoreCase))
        {
            var assets = twins.Select(i => i.AssetId!.Value).Distinct().ToList();
            var analyzed = assets.FirstOrDefault(results.ContainsKey);
            if (analyzed != 0 && photos.All(p => p.AnalyzedAssetId != analyzed))
            {
                photos.Add(new JobPhoto(analyzed, assets, photos.Count, results[analyzed]));
            }
        }

        return photos;
    }
}

/// <summary>
/// Pipeline stage 4 (barrier): bursts, bracketing and timelapse of the job's photos (<see cref="SequenceGrouper"/>),
/// stored as automatic groups; manual groups are kept (F2-4: recomputed whole on every run).
/// </summary>
public sealed class GroupingStage(ICatalog catalog, Func<string, SceneProfile> profiles, string? embeddingModelVersion) : IBatchStage
{
    public PipelineStage Stage => PipelineStage.Grouping;

    public async Task RunAsync(JobContext job, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var photos = await JobPhotos.LoadAsync(catalog, job.JobId, ct).ConfigureAwait(false);
        var ids = photos.Select(p => p.AnalyzedAssetId).ToList();
        var embeddings = embeddingModelVersion is null
            ? new Dictionary<long, float[]>()
            : await catalog.Analysis.GetEmbeddingsAsync(ids, embeddingModelVersion, ct).ConfigureAwait(false);
        var input = new List<GroupingPhoto>(photos.Count);
        foreach (var photo in photos)
        {
            var asset = await catalog.Assets.GetAsync(photo.AnalyzedAssetId, ct).ConfigureAwait(false);
            var exif = asset?.Exif;
            input.Add(new GroupingPhoto(
                photo.AnalyzedAssetId,
                photo.Order,
                asset?.CaptureDateSource == DateSource.Exif ? asset.CapturedAt : null,
                exif?.CameraModel,
                exif?.ExposureCompensationEv,
                ExposureValue(exif),
                embeddings.GetValueOrDefault(photo.AnalyzedAssetId)));
            progress?.Report(new StageProgress(Stage, input.Count, photos.Count, null));
        }

        await catalog.Groups.ReplaceAutomaticAsync(job.JobId, SequenceGrouper.Group(input, profiles(job.ProfileId).Grouping), ct).ConfigureAwait(false);
    }

    /// <summary>EV at ISO 100: log2(N²/t) − log2(ISO/100); null without aperture, time and ISO.</summary>
    public static double? ExposureValue(ExifSummary? exif) =>
        exif is { Aperture: > 0 and var n, ExposureTimeSeconds: > 0 and var t, Iso: > 0 and var iso }
            ? Math.Log2(n * n / t) - Math.Log2(iso / 100.0)
            : null;
}

/// <summary>
/// Pipeline stage 5 (per group): every photo is decided again as a single shot from its stored signals, then the bursts
/// are ranked (<see cref="BurstRanker"/>) with the profile of their photos (the job profile unless CLIP or the user
/// assigned another one, D8). Bracketing, timelapse and panorama keep the single-shot decisions. The user's decisions are never touched (I5):
/// only the automatic proposal is written. Twins get the decision of their analyzed photo.
/// </summary>
public sealed class GroupDecisionStage(ICatalog catalog, Func<string, SceneProfile> profiles, IDecisionEngine engine) : IBatchStage
{
    public PipelineStage Stage => PipelineStage.Decision;

    public async Task RunAsync(JobContext job, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var photos = (await JobPhotos.LoadAsync(catalog, job.JobId, ct).ConfigureAwait(false)).ToDictionary(p => p.AnalyzedAssetId);
        var groups = await catalog.Groups.ListAsync(job.JobId, ct).ConfigureAwait(false);
        var assigned = await catalog.Groups.GetPhotoProfilesAsync(job.JobId, ct).ConfigureAwait(false);
        SceneProfile ProfileOf(long assetId) => profiles(assigned.GetValueOrDefault(assetId)?.ProfileId ?? job.ProfileId);
        var decided = new Dictionary<long, (DecisionProposal Proposal, string Version)>();

        foreach (var photo in photos.Values)
        {
            var profile = ProfileOf(photo.AnalyzedAssetId);
            decided[photo.AnalyzedAssetId] = (engine.Decide(CullingScores.FromScores(photo.Analysis.Scores), profile), $"{engine.Version}+{profile.Id}@{profile.Version}");
        }

        foreach (var (group, members) in groups.Where(g => g.Group.IsRanked))
        {
            var profile = ProfileOf(members[0].AssetId);
            var burst = members.Where(m => photos.ContainsKey(m.AssetId))
                .Select(m => new BurstMember(m.AssetId, CullingScores.FromScores(photos[m.AssetId].Analysis.Scores), decided[m.AssetId].Proposal))
                .ToList();
            if (burst.Count < 2)
            {
                continue;
            }

            var scores = BurstRanker.Scores(burst, profile.Weights);
            await catalog.Groups.SetScoresAsync(group.Id, scores, ct).ConfigureAwait(false);
            foreach (var (assetId, proposal) in BurstRanker.Decide(burst, scores, profile.Grouping.TieMargin))
            {
                decided[assetId] = (proposal, $"{engine.Version}+{BurstRanker.Version}+{profile.Id}@{profile.Version}");
            }
        }

        var done = 0;
        foreach (var (assetId, (proposal, version)) in decided)
        {
            foreach (var twin in photos[assetId].AssetIds)
            {
                await catalog.Decisions.SetAutomaticAsync(twin, proposal.Outcome, proposal.Reasons, version, CancellationToken.None).ConfigureAwait(false);
            }

            done++;
            progress?.Report(new StageProgress(Stage, done, decided.Count, null));
        }
    }
}
