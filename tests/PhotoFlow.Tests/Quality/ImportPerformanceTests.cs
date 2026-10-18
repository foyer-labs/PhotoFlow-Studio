// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Ingest;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Sources;
using PhotoFlow.Data;
using PhotoFlow.Imaging;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Sources.Scanning;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Quality;

/// <summary>
/// Full import of the local photo set (PHOTOFLOW_TESTDATA/photos, or PHOTOFLOW_PERF_PHOTOS) with the real pipeline:
/// verified copy, previews, thumbnails, D-FINE M, buffalo_l, decisions. The source is only read (I1); the copy goes
/// to a temporary library. The report gives throughput and the time for 1,000 photos.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Performance)]
public sealed class ImportPerformanceTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-perf");

    [Fact]
    public async Task Import_throughput_is_measured_on_the_local_set()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Environment.GetEnvironmentVariable("PHOTOFLOW_PERF_PHOTOS") is { Length: > 0 } custom ? custom : TestData.RequireDirectory("photos");
        var fs = new PhysicalFileSystem();
        var before = FolderSnapshot.Take(source);
        var library = _temp.Sub("library");
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), ct);
        await using var analysis = await LocalAnalysis.CreateAsync(fs, new ThumbnailCache(fs, _temp.Sub("thumbs")), ct);
        var exif = new ExifToolExifReader(analysis.ExifTool);

        var clock = Stopwatch.StartNew();
        var scanned = new List<ScannedFile>();
        await foreach (var file in new SourceScanner(fs, source).ScanAsync(ct))
        {
            scanned.Add(file);
        }

        var plan = await new ImportPlanner(fs, exif, catalog.Assets).PlanAsync(scanned, "Performance", null, new PathTemplate(PathTemplate.Default), ct);
        var planning = clock.Elapsed;
        var jobId = await ImportJobs.CreateAsync(catalog, plan, ImportSelection.All, source,
            [new IngestDestination("library", library, DestinationKind.Primary)], "Performance", plan.ProposedEventDate, "events", PhotoFlow.Core.Analysis.FaceMode.DetectOnly, ct);
        var summary = await new ImportJobRunner(catalog, new IngestService(new CopyEngine(fs)), exif, fs, analysis.Processor).RunAsync(jobId, null, ct);
        var total = clock.Elapsed;

        var items = await catalog.Jobs.GetItemsAsync(jobId, ct);
        var photos = scanned.Where(s => s.IsAnalyzable).GroupBy(s => s.PairKey, StringComparer.OrdinalIgnoreCase).Count();
        var bytes = scanned.Sum(s => s.Item.Length);
        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"# Import performance — {DateTime.Now:yyyy-MM-dd HH:mm}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Source {source}: {scanned.Count} files ({bytes / 1e9:0.00} GB), {photos} photos analyzed; backend {analysis.Backend}; {Environment.ProcessorCount} logical CPUs.")
            .AppendLine()
            .AppendLine("| Measure | Value |")
            .AppendLine("|---|---|")
            .AppendLine(CultureInfo.InvariantCulture, $"| Scan and plan (EXIF, quick hash) | {planning.TotalSeconds:0.0} s |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Copy + verify + preview + analysis + decision | {(total - planning).TotalSeconds:0.0} s |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Total | {total.TotalSeconds:0.0} s |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Photos per second | {photos / total.TotalSeconds:0.00} |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Copy throughput (whole job) | {bytes / 1e6 / total.TotalSeconds:0} MB/s |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Estimate for 1,000 photos | {total.TotalMinutes / photos * 1000:0.0} min |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Failed files | {summary.Failed} |");
        var folder = Path.Combine(TestData.Root ?? _temp.Path, "lab", "performance");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, $"report-{DateTime.Now:yyyyMMdd-HHmmss}.md"), report.ToString(), ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());

        Assert.Equal(JobState.Completed, summary.State);
        Assert.Equal(0, summary.Failed);
        Assert.All(items, i => Assert.Equal(JobItemState.Done, i.State));
        Assert.Equal(before, FolderSnapshot.Take(source)); // I1
    }

    public void Dispose() => _temp.Dispose();
}
