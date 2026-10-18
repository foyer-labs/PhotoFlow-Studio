// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Search;
using PhotoFlow.Core.Tagging;
using PhotoFlow.Data;
using PhotoFlow.Imaging;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tagging.Vocabulary;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Quality;

/// <summary>
/// Phase 3 measurements (plan 3.9) on the local photo set, with the background settings of the earlier measures:
/// analysis with CLIP, fingerprints and RAM++ tags (level B); then the most frequent tags (to check by eye), tags per
/// photo, and a few searches on the real catalog (words in Italian, by meaning). The folder is only read.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Performance)]
public sealed class Phase3PerformanceTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-phase3-perf");

    [Fact]
    public async Task Analysis_with_tags_and_searches_on_the_local_set()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Environment.GetEnvironmentVariable("PHOTOFLOW_PERF_PHOTOS") is { Length: > 0 } custom ? custom : TestData.RequireDirectory("photos");
        var fs = new PhotoFlow.Core.IO.PhysicalFileSystem();
        var before = FolderSnapshot.Take(source);
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), ct);
        await using var analysis = await LocalAnalysis.CreateAsync(fs, new ThumbnailCache(fs, _temp.Sub("thumbs")), ct, recognition: true, tagging: true);
        await catalog.Tags.SeedVocabularyAsync(RamPlusVocabulary.Entries, TagSource.RamPlus, ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "Photos", source, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), ct);

        var workers = Math.Max(1, PipelineDefaults.DefaultAnalysisWorkers / 2);
        var jobId = await ArchiveJobRunner.CreateAsync(catalog, fs, new ArchiveJobSettings(libraryId, "events", ArchiveOrder.ByFolder, FaceMode.Recognize, workers, FullHash: false, Tags: TagLevels.Default), ct);
        var clock = Stopwatch.StartNew();
        var summary = await new ArchiveJobRunner(catalog, new ExifToolExifReader(analysis.ExifTool), fs, analysis.Processor).RunAsync(jobId, null, ct);
        var analyzed = clock.Elapsed;

        var items = await catalog.Jobs.GetItemsAsync(jobId, ct);
        var photos = items.Where(i => i.State == JobItemState.Done).GroupBy(i => Path.ChangeExtension(i.SourcePath, null), StringComparer.OrdinalIgnoreCase).Count();
        var tags = (await catalog.Tags.ListAsync(ct)).Where(t => t.PhotoCount > 0).OrderByDescending(t => t.PhotoCount).ToList();
        var assets = items.Where(i => i.AssetId is not null).Select(i => i.AssetId!.Value).Distinct().ToList();
        var perPhoto = (await catalog.Tags.GetActiveForAssetsAsync(assets, ct)).Values.Where(v => v.Count > 0).Select(v => v.Count).DefaultIfEmpty(0).ToList();

        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"# Phase 3 performance — {DateTime.Now:yyyy-MM-dd HH:mm}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Folder {source}: {items.Count} files, {photos} photos. Backend {analysis.Backend}; {workers} analysis workers (background).")
            .AppendLine("Per photo: preview, D-FINE M, buffalo_l, CLIP, ArcFace fingerprints, RAM++ fp32 tags.")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"| EXIF + preview + analysis + tags | {analyzed.TotalSeconds:0.0} s ({photos / Math.Max(analyzed.TotalSeconds, 0.001):0.00} photos/s) |")
            .AppendLine("|---|---|")
            .AppendLine(CultureInfo.InvariantCulture, $"| Estimate for 57,130 photos | {analyzed.TotalHours / Math.Max(photos, 1) * 57_130:0.0} h |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Tags per photo | mean {perPhoto.Average():0.0}, max {perPhoto.Max()} |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Distinct tags | {tags.Count} |")
            .AppendLine()
            .AppendLine("## Most frequent tags (manual check)")
            .AppendLine()
            .AppendLine(string.Join(", ", tags.Take(40).Select(t => $"{t.English} ({t.Italian}) {t.PhotoCount}")))
            .AppendLine()
            .AppendLine("## Searches on the real catalog")
            .AppendLine()
            .AppendLine("| Search | Time | Results |")
            .AppendLine("|---|---|---|");
        foreach (var word in new[] { "moto", "persona", "tramonto", "montagna", "folla" })
        {
            var result = await catalog.Search.SearchAsync(SearchQuery.Empty with { Text = word }, ct);
            report.AppendLine(CultureInfo.InvariantCulture, $"| \"{word}\" | {result.Elapsed.TotalMilliseconds:0} ms | {result.Total} |");
        }

        var folder = Path.Combine(TestData.Root ?? _temp.Path, "lab", "performance");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, $"phase3-{DateTime.Now:yyyyMMdd-HHmmss}.md"), report.ToString(), ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());

        Assert.Equal(JobState.Completed, summary.State);
        Assert.True(perPhoto.Average() > 1);
        Assert.Equal(before, FolderSnapshot.Take(source)); // the folder is only read
    }

    public void Dispose() => _temp.Dispose();
}
