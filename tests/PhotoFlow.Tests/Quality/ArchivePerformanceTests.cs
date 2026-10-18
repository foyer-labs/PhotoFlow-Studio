// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Data;
using PhotoFlow.Imaging;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Quality;

/// <summary>
/// In-place analysis of the local archive subset (PHOTOFLOW_TESTDATA/archive; the photo set when the subset is not
/// there) with the real pipeline and the background settings: half the analysis workers, D-FINE M, buffalo_l
/// (detection), decisions, full hash pass. The folder is only read. The report gives the throughput by phase and the
/// estimate for a 57,130-photo archive (board 5f), plus the damaged files found.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Performance)]
public sealed class ArchivePerformanceTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-archive-perf");

    [Fact]
    public async Task Archive_analysis_throughput_is_measured_on_the_local_subset()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = TestData.Root is { } root && Directory.Exists(Path.Combine(root, "archive"))
            ? Path.Combine(root, "archive")
            : TestData.RequireDirectory("photos");
        var fs = new PhysicalFileSystem();
        var before = FolderSnapshot.Take(source);
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), ct);
        await using var analysis = await LocalAnalysis.CreateAsync(fs, new ThumbnailCache(fs, _temp.Sub("thumbs")), ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "Archive", source, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), ct);

        var clock = Stopwatch.StartNew();
        var workers = Math.Max(1, PipelineDefaults.DefaultAnalysisWorkers / 2);
        var analyzeOnly = new ArchiveJobSettings(libraryId, "archive", ArchiveOrder.NewestFirst, FaceMode.DetectOnly, workers, FullHash: false);
        var jobId = await ArchiveJobRunner.CreateAsync(catalog, fs, analyzeOnly, ct);
        var created = clock.Elapsed;
        var summary = await new ArchiveJobRunner(catalog, new ExifToolExifReader(analysis.ExifTool), fs, analysis.Processor).RunAsync(jobId, null, ct);
        var analyzed = clock.Elapsed - created;

        // The final full-hash pass, measured on its own.
        clock.Restart();
        var hashJob = await ArchiveJobRunner.CreateAsync(catalog, fs, analyzeOnly with { FullHash = true }, ct);
        await new ArchiveJobRunner(catalog, new ExifToolExifReader(analysis.ExifTool), fs, analysis.Processor).RunAsync(hashJob, null, ct);
        var hashed = clock.Elapsed;

        var items = await catalog.Jobs.GetItemsAsync(jobId, ct);
        var formats = await catalog.Libraries.CountFormatsAsync(libraryId, ct);
        var photos = items.Where(i => i.State == JobItemState.Done).GroupBy(i => Path.ChangeExtension(i.SourcePath, null), StringComparer.OrdinalIgnoreCase).Count();
        var bytes = items.Sum(i => new FileInfo(i.SourcePath).Length);
        var failed = items.Where(i => i.State == JobItemState.Failed).ToList();
        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"# Archive analysis performance — {DateTime.Now:yyyy-MM-dd HH:mm}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Folder {source}: {items.Count} files ({bytes / 1e9:0.00} GB), {photos} photos; formats {string.Join(", ", formats.OrderByDescending(f => f.Value).Select(f => $"{f.Key} {f.Value}"))}.")
            .AppendLine(CultureInfo.InvariantCulture, $"Backend {analysis.Backend}; {Environment.ProcessorCount} logical CPUs; {workers} analysis workers (background, half of an import).")
            .AppendLine()
            .AppendLine("| Measure | Value |")
            .AppendLine("|---|---|")
            .AppendLine(CultureInfo.InvariantCulture, $"| Scan and job creation | {created.TotalSeconds:0.0} s |")
            .AppendLine(CultureInfo.InvariantCulture, $"| EXIF + quick hash + preview + analysis + decision | {analyzed.TotalSeconds:0.0} s |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Photos per second | {photos / analyzed.TotalSeconds:0.00} |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Full hash pass (whole folder) | {hashed.TotalSeconds:0.0} s ({bytes / 1e6 / Math.Max(hashed.TotalSeconds, 0.001):0} MB/s) |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Estimate for 57,130 photos | {analyzed.TotalHours / Math.Max(photos, 1) * 57_130:0.0} h |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Damaged files (in the report) | {failed.Count} |");
        foreach (var item in failed)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"- {Path.GetRelativePath(source, item.SourcePath)}: {item.Error}");
        }

        var folder = Path.Combine(TestData.Root ?? _temp.Path, "lab", "performance");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, $"archive-{DateTime.Now:yyyyMMdd-HHmmss}.md"), report.ToString(), ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());

        Assert.Equal(JobState.Completed, summary.State);
        Assert.Empty(await catalog.Assets.ListWithoutFullHashAsync(libraryId, 10, ct));
        Assert.Equal(before, FolderSnapshot.Take(source)); // the archive is only read
    }

    public void Dispose() => _temp.Dispose();
}
