// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Culling;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Pipeline;

namespace PhotoFlow.Analysis.Grouping;

/// <summary>
/// Scene profile per group (D8), after the grouping: each group (mean embedding of its photos) and each single photo is
/// classified with CLIP. Automatic mode: the most probable profile when its probability reaches
/// <see cref="AutomaticMinConfidence"/>, otherwise the job's fallback profile. Manual mode (default): the job profile
/// stays, and a different profile reaching <see cref="SuggestMinConfidence"/> is stored as a suggestion for the review.
/// A profile chosen by the user is never replaced. Without the CLIP model nothing is assigned.
/// </summary>
public sealed class ProfileStage(ICatalog catalog, ISceneClassifier? classifier, string? embeddingModelVersion) : IBatchStage
{
    public const double AutomaticMinConfidence = 0.5;
    public const double SuggestMinConfidence = 0.6;

    public PipelineStage Stage => PipelineStage.Grouping;

    public async Task RunAsync(JobContext job, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (classifier is null || embeddingModelVersion is null)
        {
            return;
        }

        var record = await catalog.Jobs.GetAsync(job.JobId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Job {job.JobId} not found.");
        var photos = await JobPhotos.LoadAsync(catalog, job.JobId, ct).ConfigureAwait(false);
        var embeddings = await catalog.Analysis.GetEmbeddingsAsync(photos.Select(p => p.AnalyzedAssetId).ToList(), embeddingModelVersion, ct).ConfigureAwait(false);
        var groups = await catalog.Groups.ListAsync(job.JobId, ct).ConfigureAwait(false);
        var grouped = groups.SelectMany(g => g.Members.Select(m => m.AssetId)).ToHashSet();
        var units = groups.Select(g => (Group: (long?)g.Group.Id, Assets: (IReadOnlyList<long>)g.Members.Select(m => m.AssetId).Where(embeddings.ContainsKey).ToList()))
            .Concat(photos.Where(p => !grouped.Contains(p.AnalyzedAssetId) && embeddings.ContainsKey(p.AnalyzedAssetId))
                .Select(p => ((long?)null, (IReadOnlyList<long>)[p.AnalyzedAssetId])))
            .Where(u => u.Item2.Count > 0)
            .ToList();

        var rows = new List<PhotoProfile>();
        var done = 0;
        foreach (var (groupId, assets) in units)
        {
            var ranking = await classifier.ClassifyAsync(Vectors.Mean(assets.Select(a => embeddings[a]).ToList()), ct).ConfigureAwait(false);
            var (top, probability) = ranking[0];
            string? profile = null;
            double? confidence = null;
            string? suggested = null;
            double? suggestedConfidence = null;
            if (record.AutomaticProfile)
            {
                (profile, confidence) = probability >= AutomaticMinConfidence ? (top, probability) : (record.FallbackProfileId ?? job.ProfileId, probability);
            }
            else if (top != job.ProfileId && probability >= SuggestMinConfidence)
            {
                (suggested, suggestedConfidence) = (top, probability);
            }

            rows.AddRange(assets.Select(a => new PhotoProfile(job.JobId, a, profile, confidence, false, suggested, suggestedConfidence)));
            if (groupId is { } id && record.AutomaticProfile)
            {
                await catalog.Groups.SetProfileAsync(id, profile, confidence, ct).ConfigureAwait(false);
            }

            done++;
            progress?.Report(new StageProgress(Stage, done, units.Count, null));
        }

        await catalog.Groups.SetAutomaticProfilesAsync(job.JobId, rows, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Analyzes again, with their profile, the photos whose signals were measured with another one (the sharpness zone
/// depends on the profile: AF point and faces for events, the vehicle for motorsport…). The original file is read
/// where the catalog knows it (library copy, or the folder of an in-place analysis); faces with a manual assignment
/// are kept; the user's decisions are untouched (I5). A photo whose new profile measures like the old one
/// (<see cref="CullingScores.MeasurementKey"/>) keeps its signals and only changes profile; the others are analyzed
/// <paramref name="parallelism"/> at a time.
/// </summary>
public sealed class ReanalysisStage(ICatalog catalog, IFileSystem fileSystem, IPhotoProcessor processor, FaceMode? faces,
    Func<string, SceneProfile>? resolveProfile = null, int parallelism = 1) : IBatchStage
{
    public PipelineStage Stage => PipelineStage.Analyzers;

    public async Task RunAsync(JobContext job, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var photos = await JobPhotos.LoadAsync(catalog, job.JobId, ct).ConfigureAwait(false);
        var profiles = await catalog.Groups.GetPhotoProfilesAsync(job.JobId, ct).ConfigureAwait(false);
        var pending = photos.Where(p => (profiles.GetValueOrDefault(p.AnalyzedAssetId)?.ProfileId ?? job.ProfileId) != p.Analysis.ProfileId).ToList();
        var done = 0;
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = ct }, async (photo, token) =>
        {
            var profileId = profiles.GetValueOrDefault(photo.AnalyzedAssetId)?.ProfileId ?? job.ProfileId;
            var analysis = photo.Analysis;
            if (resolveProfile is { } resolve && analysis.ProfileId is { } measuredWith
                && CullingScores.MeasurementKey(resolve(measuredWith)) == CullingScores.MeasurementKey(resolve(profileId)))
            {
                await catalog.Analysis.SaveResultAsync(analysis with { Id = 0, ProfileId = profileId }, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var asset = await catalog.Assets.GetAsync(photo.AnalyzedAssetId, token).ConfigureAwait(false);
                if (await AssetFiles.LocateAsync(catalog, fileSystem, photo.AnalyzedAssetId, token).ConfigureAwait(false) is not { } path || asset is null)
                {
                    return; // the file is not reachable now: the photo keeps its signals until a later run
                }

                var result = await processor.ProcessAsync(new PhotoInput(path, asset.FullHash ?? asset.QuickHash, asset.Exif), new ProcessingOptions(profileId, faces), token).ConfigureAwait(false);
                await catalog.Analysis.SaveResultAsync(new AnalyzerResultRecord(0, photo.AnalyzedAssetId, result.Analysis.AnalyzerId, result.Analysis.ModelVersion, profileId, result.Analysis.Scores), CancellationToken.None).ConfigureAwait(false);
                await catalog.Analysis.ReplaceAutomaticFacesAsync(photo.AnalyzedAssetId, result.Faces, CancellationToken.None).ConfigureAwait(false);
            }

            progress?.Report(new StageProgress(Stage, Interlocked.Increment(ref done), pending.Count, null));
        }).ConfigureAwait(false);
    }
}
