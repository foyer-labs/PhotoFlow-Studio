// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using PhotoFlow.Analysis.Culling;
using PhotoFlow.Analysis.Grouping;
using PhotoFlow.Analysis.Profiles;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Data;
using PhotoFlow.Faces.Recognition;
using PhotoFlow.Imaging;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Quality;

/// <summary>
/// Phase 2 measurements (plan 2.12) on the local photo set, with the background settings of the archive measure of
/// phase 1 for comparison: per-photo analysis with CLIP embeddings and encrypted ArcFace fingerprints (Recognize), then
/// the batch stages in automatic-profile mode (grouping, CLIP profile per group, re-analysis, decisions per group,
/// recognition and clustering). The report gives the time of each part, the groups found, the faces and the
/// people, and the CLIP scene of each photo for a manual sanity check. The folder is only read.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Performance)]
public sealed class Phase2PerformanceTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-phase2-perf");

    [Fact]
    public async Task Analysis_with_clip_and_fingerprints_and_batch_stages_is_measured()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Environment.GetEnvironmentVariable("PHOTOFLOW_PERF_PHOTOS") is { Length: > 0 } custom ? custom : TestData.RequireDirectory("photos");
        var fs = new PhysicalFileSystem();
        var before = FolderSnapshot.Take(source);
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), ct);
        await using var analysis = await LocalAnalysis.CreateAsync(fs, new ThumbnailCache(fs, _temp.Sub("thumbs")), ct, recognition: true);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "Photos", source, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), ct);

        var workers = Math.Max(1, PipelineDefaults.DefaultAnalysisWorkers / 2);
        var settings = new ArchiveJobSettings(libraryId, "events", ArchiveOrder.ByFolder, FaceMode.Recognize, workers, FullHash: false);
        var jobId = await ArchiveJobRunner.CreateAsync(catalog, fs, settings, ct);
        await catalog.Jobs.SetAutomaticProfileAsync(jobId, true, "events", ct);
        IReadOnlyList<IBatchStage> stages =
        [
            new GroupingStage(catalog, BuiltInProfiles.Get, analysis.EmbeddingVersion),
            new ProfileStage(catalog, analysis.SceneClassifier, analysis.EmbeddingVersion),
            new ReanalysisStage(catalog, fs, analysis.Processor, FaceMode.Recognize, BuiltInProfiles.Get, workers),
            new GroupDecisionStage(catalog, BuiltInProfiles.Get, new DecisionEngine()),
            new FacesStage(catalog, () => analysis.Protector),
        ];

        // Time of each part, from the stage reported by the runner.
        var clock = Stopwatch.StartNew();
        var marks = new List<(PipelineStage Stage, TimeSpan At)>();
        var progress = new SynchronousProgress<JobProgress>(p =>
        {
            lock (marks)
            {
                if (marks.Count == 0 || marks[^1].Stage != p.Stage)
                {
                    marks.Add((p.Stage, clock.Elapsed));
                }
            }
        });
        var summary = await new ArchiveJobRunner(catalog, new ExifToolExifReader(analysis.ExifTool), fs, analysis.Processor, stages).RunAsync(jobId, progress, ct);
        var total = clock.Elapsed;

        var items = await catalog.Jobs.GetItemsAsync(jobId, ct);
        var photos = items.Where(i => i.State == JobItemState.Done).GroupBy(i => Path.ChangeExtension(i.SourcePath, null), StringComparer.OrdinalIgnoreCase).Count();
        var batchStart = marks.FirstOrDefault(m => m.Stage is PipelineStage.Grouping).At;
        var perPhoto = batchStart > TimeSpan.Zero ? batchStart : total;
        var groups = await catalog.Groups.ListAsync(jobId, ct);
        var assetIds = items.Where(i => i.AssetId is not null).Select(i => i.AssetId!.Value).Distinct().ToList();
        var faces = new List<Face>();
        foreach (var id in assetIds)
        {
            faces.AddRange(await catalog.Analysis.GetFacesAsync(id, ct));
        }

        var persons = await catalog.People.ListPersonsAsync(ct);
        var profiles = await catalog.Groups.GetPhotoProfilesAsync(jobId, ct);

        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"# Phase 2 performance — {DateTime.Now:yyyy-MM-dd HH:mm}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Folder {source}: {items.Count} files, {photos} photos. Backend {analysis.Backend}; {Environment.ProcessorCount} logical CPUs; {workers} analysis workers (background).")
            .AppendLine("Per photo: preview, D-FINE M, buffalo_l detection and eyes, CLIP ViT-B/32 image embedding, ArcFace fingerprints (AES-GCM). Job in automatic-profile mode.")
            .AppendLine()
            .AppendLine("| Part | Time |")
            .AppendLine("|---|---|")
            .AppendLine(CultureInfo.InvariantCulture, $"| Scan, EXIF, per-photo analysis | {perPhoto.TotalSeconds:0.0} s ({photos / Math.Max(perPhoto.TotalSeconds, 0.001):0.00} photos/s) |");
        var firstBatch = marks.FindIndex(m => m.Stage is PipelineStage.Grouping);
        for (var i = Math.Max(firstBatch, 0); firstBatch >= 0 && i < marks.Count; i++)
        {
            var end = i + 1 < marks.Count ? marks[i + 1].At : total;
            var name = marks[i].Stage == PipelineStage.Analyzers ? "Re-analysis with the group profile" : $"Batch stage {marks[i].Stage}";
            report.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {(end - marks[i].At).TotalSeconds:0.00} s |");
        }

        report
            .AppendLine(CultureInfo.InvariantCulture, $"| Total | {total.TotalSeconds:0.0} s |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Estimate for 57,130 photos | {total.TotalHours / Math.Max(photos, 1) * 57_130:0.0} h |")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Groups: {string.Join(", ", groups.GroupBy(g => g.Group.Type).Select(g => $"{g.Key} {g.Count()} ({g.Sum(x => x.Members.Count)} photos)"))}.")
            .AppendLine(CultureInfo.InvariantCulture, $"Faces: {faces.Count}, with fingerprint {faces.Count(f => f.EncryptedEmbedding is not null)}, assigned {faces.Count(f => f.PersonId is not null)}; people (clusters) {persons.Count}.")
            .AppendLine(CultureInfo.InvariantCulture, $"Profiles (automatic): {string.Join(", ", profiles.Values.GroupBy(p => p.ProfileId ?? "-").OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}.")
            .AppendLine()
            .AppendLine("## CLIP scene per photo (manual check)")
            .AppendLine()
            .AppendLine("| Photo | Profile | Confidence |")
            .AppendLine("|---|---|---|");
        foreach (var item in items.Where(i => i.AssetId is { } id && profiles.ContainsKey(id)).OrderBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var p = profiles[item.AssetId!.Value];
            report.AppendLine(CultureInfo.InvariantCulture, $"| {Path.GetFileName(item.SourcePath)} | {p.ProfileId ?? "-"} | {p.Confidence?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-"} |");
        }

        var folder = Path.Combine(TestData.Root ?? _temp.Path, "lab", "performance");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, $"phase2-{DateTime.Now:yyyyMMdd-HHmmss}.md"), report.ToString(), ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());

        Assert.Equal(JobState.Completed, summary.State);
        Assert.Contains(faces, f => f.EncryptedEmbedding is not null);
        Assert.Equal(before, FolderSnapshot.Take(source)); // the folder is only read
    }

    public void Dispose() => _temp.Dispose();

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
