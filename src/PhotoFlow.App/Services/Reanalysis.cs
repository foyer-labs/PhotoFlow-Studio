// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Tagging;
using PhotoFlow.Faces.Recognition;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tagging.Ocr;

namespace PhotoFlow.App.Services;

/// <summary>What the confirmation of a regeneration changed.</summary>
/// <param name="Reset">What "Ripartire da zero con le persone" removed (P22), when the user chose it.</param>
public sealed record ReanalysisApplied(int Photos, int Transferred, int XmpWritten, PeopleReset? Reset = null);

/// <summary>
/// Preview and confirmation of a regeneration (spec §5): the staged results are compared with the catalog (counts and
/// examples), then applied as a whole with the protections of I5 (the user's decisions, manual and suppressed tags,
/// the user's captions and face assignments stay), or discarded. Faces of a new model take over the assignments of
/// the old ones (<see cref="FaceTransfer"/>); the new faces are then recognized and grouped; the numbers of the jobs
/// whose OCR changed are matched again; the XMP already written is written again.
/// </summary>
public static class Reanalysis
{
    private static JsonSerializerOptions Json => ReanalysisJobRunner.Json;

    public static async Task<ReanalysisPreview> PreviewAsync(ICatalog catalog, long jobId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var staged = await catalog.Reanalysis.ListAsync(jobId, ct).ConfigureAwait(false);
        int outcomes = 0, userKept = 0, tagsAdded = 0, tagsRemoved = 0, tagsProtected = 0, transferred = 0, faceNew = 0, lost = 0, keptManual = 0;
        int captions = 0, captionsProtected = 0, embeddings = 0, ocr = 0;
        var examples = new List<string>();
        foreach (var asset in staged.GroupBy(s => s.AssetId))
        {
            var notes = new List<string>();
            foreach (var (_, part, json) in asset)
            {
                switch (part)
                {
                    case ReanalysisParts.Culling:
                        var culling = JsonSerializer.Deserialize<StagedCulling>(json, Json)!;
                        var decision = await catalog.Decisions.GetAsync(asset.Key, ct).ConfigureAwait(false);
                        if (decision?.AutomaticOutcome != culling.Decision.Outcome)
                        {
                            outcomes++;
                            if (decision?.UserOutcome is not null)
                            {
                                userKept++;
                            }

                            notes.Add($"{decision?.AutomaticOutcome?.ToString() ?? "—"} → {culling.Decision.Outcome}");
                        }

                        break;
                    case ReanalysisParts.Tags:
                        var keys = JsonSerializer.Deserialize<List<TagResult>>(json, Json)!.Select(t => t.TagKey).ToHashSet(StringComparer.Ordinal);
                        var tags = await catalog.Tags.GetForAssetAsync(asset.Key, ct).ConfigureAwait(false);
                        var automatic = tags.Where(t => t.Assignment.Source == TagSource.RamPlus && t.Assignment.State == AssetTagState.Active).Select(t => t.Tag.Key).ToHashSet(StringComparer.Ordinal);
                        var blocked = tags.Where(t => t.Assignment.State == AssetTagState.Suppressed || t.Assignment.Source == TagSource.Manual).Select(t => t.Tag.Key).ToHashSet(StringComparer.Ordinal);
                        var added = keys.Where(k => !automatic.Contains(k) && !blocked.Contains(k)).ToList();
                        var removed = automatic.Where(k => !keys.Contains(k)).ToList();
                        tagsAdded += added.Count;
                        tagsRemoved += removed.Count;
                        tagsProtected += keys.Count(blocked.Contains);
                        notes.AddRange(added.Take(3).Select(k => "+" + k));
                        notes.AddRange(removed.Take(3).Select(k => "−" + k));
                        break;
                    case ReanalysisParts.Faces:
                        var fresh = JsonSerializer.Deserialize<List<Face>>(json, Json)!;
                        var old = await catalog.Analysis.GetFacesAsync(asset.Key, ct).ConfigureAwait(false);
                        var matches = FaceTransfer.Match(old, fresh);
                        var matched = matches.OfType<long>().ToHashSet();
                        transferred += matches.OfType<long>().Count(id => old.First(f => f.Id == id).PersonId is not null);
                        faceNew += matches.Count(m => m is null);
                        lost += old.Count(f => !matched.Contains(f.Id) && f.AssignmentOrigin != AssignmentOrigin.Manual);
                        keptManual += old.Count(f => !matched.Contains(f.Id) && f.AssignmentOrigin == AssignmentOrigin.Manual);
                        break;
                    case ReanalysisParts.Captions:
                        var current = (await catalog.Captions.GetAllAsync(asset.Key, ct).ConfigureAwait(false)).ToDictionary(c => c.Language);
                        foreach (var caption in JsonSerializer.Deserialize<List<GeneratedCaption>>(json, Json)!)
                        {
                            var existing = current.GetValueOrDefault(caption.Language);
                            if (existing is { Origin: CaptionOrigin.Manual })
                            {
                                captionsProtected++;
                            }
                            else if (existing?.Text != caption.Text)
                            {
                                captions++;
                            }
                        }

                        break;
                    case ReanalysisParts.Embedding:
                        embeddings++;
                        break;
                    case ReanalysisParts.Ocr:
                        var readings = JsonSerializer.Deserialize<List<OcrReading>>(json, Json)!.Select(r => r.Text).Order(StringComparer.Ordinal);
                        var before = (await catalog.Motorsport.GetReadingsAsync(asset.Key, ct).ConfigureAwait(false)).Select(r => r.Text).Order(StringComparer.Ordinal);
                        if (!readings.SequenceEqual(before))
                        {
                            ocr++;
                        }

                        break;
                }
            }

            if (notes.Count > 0 && examples.Count < 8)
            {
                var name = await AssetFiles.LocateAsync(catalog, NoFiles.Instance, asset.Key, ct).ConfigureAwait(false) is { } path ? Path.GetFileName(path) : $"#{asset.Key}";
                examples.Add($"{name}: {string.Join(", ", notes)}");
            }
        }

        return new ReanalysisPreview(staged.Select(s => s.AssetId).Distinct().Count(), outcomes, userKept, tagsAdded, tagsRemoved, tagsProtected,
            transferred, faceNew, lost, keptManual, captions, captionsProtected, embeddings, ocr, examples);
    }

    public static async Task<ReanalysisApplied> ApplyAsync(ICatalog catalog, long jobId, Func<IFingerprintProtector> protector, XmpPublisher? publisher, bool prudentMode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(protector);
        var job = await catalog.Jobs.GetAsync(jobId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Job {jobId} not found.");
        var staged = await catalog.Reanalysis.ListAsync(jobId, ct).ConfigureAwait(false);
        var settings = ReanalysisJobRunner.DeserializeSettings(job.SettingsSnapshotJson);

        // P22: the people go first, so the new faces start from nothing (never on a sample, never without the faces).
        var reset = settings.ResetPeople && settings.Sample is null && settings.Parts.HasFlag(ReanalysisParts.Faces)
            ? await catalog.People.ResetAsync(ct).ConfigureAwait(false)
            : null;
        int transferred = 0;
        var faces = false;
        var ocrAssets = new List<long>();
        foreach (var (assetId, part, json) in staged)
        {
            switch (part)
            {
                case ReanalysisParts.Culling:
                    var culling = JsonSerializer.Deserialize<StagedCulling>(json, Json)!;
                    await catalog.Analysis.SaveResultAsync(new AnalyzerResultRecord(0, assetId, culling.Analysis.AnalyzerId, culling.Analysis.ModelVersion, culling.ProfileId,
                        culling.Analysis.Scores), ct).ConfigureAwait(false);
                    await catalog.Decisions.SetAutomaticAsync(assetId, culling.Decision.Outcome, culling.Decision.Reasons, culling.DecisionVersion, ct).ConfigureAwait(false);
                    break;
                case ReanalysisParts.Embedding:
                    var embedding = JsonSerializer.Deserialize<ImageEmbedding>(json, Json)!;
                    await catalog.Analysis.SaveEmbeddingAsync(assetId, embedding.ModelVersion, embedding.Vector, ct).ConfigureAwait(false);
                    break;
                case ReanalysisParts.Faces:
                    var result = await FaceTransfer.ApplyAsync(catalog, assetId, JsonSerializer.Deserialize<List<Face>>(json, Json)!, ct).ConfigureAwait(false);
                    transferred += result.Transferred;
                    faces = true;
                    break;
                case ReanalysisParts.Tags:
                    var tags = JsonSerializer.Deserialize<List<TagResult>>(json, Json)!;
                    await catalog.Tags.ReplaceAutomaticAsync(assetId, TagSource.RamPlus, tags.FirstOrDefault()?.ModelVersion,
                        [.. tags.Select(t => (t.TagKey, (double?)t.Confidence))], ct).ConfigureAwait(false);
                    break;
                case ReanalysisParts.Captions:
                    foreach (var caption in JsonSerializer.Deserialize<List<GeneratedCaption>>(json, Json)!)
                    {
                        await catalog.Captions.SetAutomaticAsync(assetId, caption.Language, caption.Text, caption.ModelVersion, ct).ConfigureAwait(false);
                    }

                    break;
                case ReanalysisParts.Ocr:
                    var readings = JsonSerializer.Deserialize<List<OcrReading>>(json, Json)!;
                    await catalog.Motorsport.ReplaceReadingsAsync(assetId, [.. readings.Select(r => r with { AssetId = assetId })], ct).ConfigureAwait(false);
                    ocrAssets.Add(assetId);
                    break;
            }
        }

        var context = new JobContext(jobId, JobType.Reanalyze, job.ProfileId ?? "events");
        if (faces)
        {
            await new FacesStage(catalog, protector).RunAsync(context, null, ct).ConfigureAwait(false);
        }

        if (reset is not null)
        {
            await ClusterEveryYearAsync(catalog, protector(), ct).ConfigureAwait(false);
        }

        if (ocrAssets.Count > 0)
        {
            // The numbers are matched in the job that imported the photos (its entry list).
            var sourceJobs = new HashSet<long>();
            foreach (var recent in await catalog.Jobs.ListRecentAsync(int.MaxValue, ct).ConfigureAwait(false))
            {
                if (recent.Type != JobType.Reanalyze && (await catalog.Jobs.GetItemsAsync(recent.Id, ct).ConfigureAwait(false)).Any(i => i.AssetId is { } a && ocrAssets.Contains(a)))
                {
                    sourceJobs.Add(recent.Id);
                }
            }

            foreach (var sourceJob in sourceJobs)
            {
                var source = (await catalog.Jobs.GetAsync(sourceJob, ct).ConfigureAwait(false))!;
                await new NumbersStage(catalog).RunAsync(new JobContext(sourceJob, source.Type, source.ProfileId ?? "motorsport"), null, ct).ConfigureAwait(false);
            }
        }

        var photos = staged.Select(s => s.AssetId).Distinct().ToList();
        await catalog.Reanalysis.ClearAsync(jobId, ct).ConfigureAwait(false);

        // The XMP already written follows the new results; after a reset, the photos of the removed people too (their
        // names and regions leave the files, the regions of other programs stay).
        var written = 0;
        if (publisher is not null)
        {
            var candidates = new List<long>(photos);
            if (reset is not null)
            {
                foreach (var assetId in reset.AssetIds)
                {
                    candidates.Add(assetId);
                    if (await catalog.Assets.GetTwinAsync(assetId, ct).ConfigureAwait(false) is { } twin)
                    {
                        candidates.Add(twin);
                    }
                }
            }

            var toWrite = new List<long>();
            foreach (var assetId in candidates.Distinct())
            {
                if ((await catalog.XmpSync.GetAsync(assetId, ct).ConfigureAwait(false))?.LastWrittenAt is not null)
                {
                    await catalog.XmpSync.MarkPendingAsync(assetId, null, ct).ConfigureAwait(false);
                    toWrite.Add(assetId);
                }
            }

            if (toWrite.Count > 0)
            {
                written = (await publisher.WriteAsync(toWrite, prudentMode, null, reset?.Names, ct).ConfigureAwait(false)).Written;
            }
        }

        return new ReanalysisApplied(photos.Count, transferred, written, reset);
    }

    /// <summary>After a reset the unassigned faces of every year are grouped again, not only those of the regenerated photos.</summary>
    private static async Task ClusterEveryYearAsync(ICatalog catalog, IFingerprintProtector keys, CancellationToken ct)
    {
        if (!keys.IsAvailable)
        {
            return;
        }

        var years = (await catalog.People.ListAllFingerprintsAsync(ct).ConfigureAwait(false)).Select(f => f.Year).Distinct().ToList();
        var touched = new HashSet<long>();
        foreach (var year in years)
        {
            touched.UnionWith(await new PeopleEngine(catalog, keys).ClusterYearAsync(year, ct).ConfigureAwait(false));
        }

        foreach (var personId in touched)
        {
            await PeopleEngine.RefreshPrototypesAsync(catalog, personId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The previous results stay; the staged ones go.</summary>
    public static Task DiscardAsync(ICatalog catalog, long jobId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Reanalysis.ClearAsync(jobId, ct);
    }

    /// <summary>For the names of the examples: the first location of the photo, without touching the disk.</summary>
    private sealed class NoFiles : Core.IO.IFileSystem
    {
        public static NoFiles Instance { get; } = new();

        public bool FileExists(string path) => true;

        public bool DirectoryExists(string path) => true;

        public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption option) => [];

        public Core.IO.FileMetadata GetFileMetadata(string path) => throw new NotSupportedException();

        public Stream OpenRead(string path) => throw new NotSupportedException();

        public Stream OpenWrite(string path) => throw new NotSupportedException();

        public void CreateDirectory(string path) => throw new NotSupportedException();

        public void Move(string sourcePath, string destinationPath) => throw new NotSupportedException();

        public void Replace(string replacementPath, string destinationPath, string? backupPath) => throw new NotSupportedException();

        public long GetAvailableFreeSpace(string path) => 0;
    }
}
