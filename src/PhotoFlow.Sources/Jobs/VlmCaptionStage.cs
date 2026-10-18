// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Tagging;

namespace PhotoFlow.Sources.Jobs;

/// <summary>
/// Level D of a job (phase 5b, U5): once every photo is analyzed, the photos of a job that asked for it get an Italian
/// caption, in a pass of their own with the GPU free of the other models (<see cref="IExclusiveCaptioning"/>). A photo
/// whose Italian caption is the user's (I5) or already comes from this model is skipped, so a resumed job only does
/// what is missing. A photo that cannot be read or captioned is left without, the others go on; after
/// <see cref="MaxConsecutiveFailures"/> failures in a row (the model does not start, the GPU memory is not enough) the
/// pass stops and the photos left wait for the next one: the job is not failed. Files are only read.
/// </summary>
public sealed class VlmCaptionStage(ICatalog catalog, IFileSystem fileSystem, IPreviewProvider previews, IExclusiveCaptioning vlm) : IBatchStage
{
    public const int MaxConsecutiveFailures = 3;

    public PipelineStage Stage => PipelineStage.TaggingAndOcr;

    public async Task RunAsync(JobContext job, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (vlm.ModelVersion is not { } version || await catalog.Jobs.GetAsync(job.JobId, ct).ConfigureAwait(false) is not { } record
            || !Levels(record).HasFlag(TagLevels.VlmCaption))
        {
            return;
        }

        var assets = new List<long>();
        foreach (var assetId in (await catalog.Jobs.GetItemsAsync(job.JobId, ct).ConfigureAwait(false))
                     .Where(i => i.State == JobItemState.Done && i.AssetId is not null).Select(i => i.AssetId!.Value).Distinct())
        {
            if (await NeedsCaptionAsync(catalog, assetId, vlm.Language, version, ct).ConfigureAwait(false))
            {
                assets.Add(assetId);
            }
        }

        if (assets.Count == 0)
        {
            return;
        }

        await vlm.RunAsync(async captioner =>
        {
            int done = 0, failures = 0;
            foreach (var assetId in assets)
            {
                if (await CaptionAsync(catalog, fileSystem, previews, captioner, assetId, ct).ConfigureAwait(false) is { } text)
                {
                    await catalog.Captions.SetAutomaticAsync(assetId, captioner.Language, text, captioner.ModelVersion, CancellationToken.None).ConfigureAwait(false);
                    failures = 0;
                }
                else if (++failures >= MaxConsecutiveFailures)
                {
                    break;
                }

                progress?.Report(new StageProgress(Stage, ++done, assets.Count, null));
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>No caption in that language yet, or an automatic one of another model; never over the user's (I5).</summary>
    public static async Task<bool> NeedsCaptionAsync(ICatalog catalog, long assetId, string language, string version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var existing = (await catalog.Captions.GetAllAsync(assetId, ct).ConfigureAwait(false)).FirstOrDefault(c => c.Language == language);
        return existing is null || (existing.Origin == CaptionOrigin.Automatic && existing.ModelVersion != version);
    }

    /// <summary>The caption of one photo from its preview, or null when the photo cannot be read or captioned.</summary>
    public static async Task<string?> CaptionAsync(ICatalog catalog, IFileSystem fileSystem, IPreviewProvider previews, IImageCaptioner captioner, long assetId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(previews);
        ArgumentNullException.ThrowIfNull(captioner);
        try
        {
            if (await AssetFiles.LocateAsync(catalog, fileSystem, assetId, ct).ConfigureAwait(false) is not { } path)
            {
                return null;
            }

            var preview = await previews.GetPreviewAsync(path, CullingSignals.ReferenceLongSide, ct).ConfigureAwait(false);
            var text = await captioner.CaptionAsync(preview.Image, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException) // ImageDecodeException is an IOException
        {
            return null; // the others go on; the photo is captioned again by the next pass
        }
    }

    /// <summary>The tagging levels of the job (its settings, camelCase JSON); the default when not recorded.</summary>
    public static TagLevels Levels(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            using var document = JsonDocument.Parse(job.SettingsSnapshotJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Number
                ? (TagLevels)tags.GetInt32()
                : TagLevels.Default;
        }
        catch (JsonException)
        {
            return TagLevels.Default;
        }
    }
}
