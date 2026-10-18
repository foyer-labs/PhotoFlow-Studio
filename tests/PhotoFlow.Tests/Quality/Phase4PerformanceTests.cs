// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Data;
using PhotoFlow.Imaging;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tagging.Ocr;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Quality;

/// <summary>
/// Phase 4 measurements (plan 4.6) on the local motorsport photos (a motor show of 2015 by default; another folder with
/// PHOTOFLOW_PERF_MOTORSPORT): the cost of the OCR (analysis with the Motorsport profile with and without it), the
/// readings, and the numbers chosen without an entry list — at a show with no race numbers none should be certain.
/// The photos are copied to a temporary library; the source folder is only read.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Performance)]
public sealed class Phase4PerformanceTests : IDisposable
{
    private static readonly int Workers = Math.Max(1, PipelineDefaults.DefaultAnalysisWorkers / 2);

    private readonly TempFolder _temp = new("pf-phase4-perf");

    [Fact]
    public async Task Ocr_cost_and_numbers_on_the_local_motorsport_photos()
    {
        var ct = TestContext.Current.CancellationToken;
        var custom = Environment.GetEnvironmentVariable("PHOTOFLOW_PERF_MOTORSPORT") is { Length: > 0 } folder ? folder : null;
        var source = custom ?? TestData.RequireDirectory("photos");
        var pattern = custom is null ? "20151122_*.jpg" : "*";
        var before = FolderSnapshot.Take(source);
        var files = Directory.EnumerateFiles(source, pattern).Order(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.NotEmpty(files);
        var fs = new PhotoFlow.Core.IO.PhysicalFileSystem();

        async Task<(TimeSpan Elapsed, ICatalog Catalog, long JobId)> RunAsync(string name, bool ocr)
        {
            var library = _temp.Sub(name);
            foreach (var file in files)
            {
                File.Copy(file, Path.Combine(library, Path.GetFileName(file)));
            }

            var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, name + ".pfdb"), _temp.Sub(name + "-b")), ct);
            await using var analysis = await LocalAnalysis.CreateAsync(fs, new ThumbnailCache(fs, _temp.Sub(name + "-thumbs")), ct, ocr: ocr);
            var runner = new ArchiveJobRunner(catalog, new ExifToolExifReader(analysis.ExifTool), fs, analysis.Processor);

            // Warm-up (first inferences on the GPU) on two photos of another library, not measured.
            var warm = _temp.Sub(name + "-warm");
            foreach (var file in files.Take(2))
            {
                File.Copy(file, Path.Combine(warm, Path.GetFileName(file)));
            }

            var warmId = await catalog.Libraries.AddAsync(new Library(0, name + " warm-up", warm, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), ct);
            await runner.RunAsync(await ArchiveJobRunner.CreateAsync(catalog, fs, new ArchiveJobSettings(warmId, "motorsport", ArchiveOrder.ByFolder, FaceMode.DetectOnly, Workers, FullHash: false), ct), null, ct);

            var libraryId = await catalog.Libraries.AddAsync(new Library(0, name, library, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), ct);
            var jobId = await ArchiveJobRunner.CreateAsync(catalog, fs, new ArchiveJobSettings(libraryId, "motorsport", ArchiveOrder.ByFolder, FaceMode.DetectOnly, Workers, FullHash: false), ct);
            var clock = Stopwatch.StartNew();
            var summary = await runner.RunAsync(jobId, null, ct);
            Assert.Equal(JobState.Completed, summary.State);
            return (clock.Elapsed, catalog, jobId);
        }

        var (plain, plainCatalog, _) = await RunAsync("without-ocr", ocr: false);
        await plainCatalog.DisposeAsync();
        var (withOcr, catalog, jobId) = await RunAsync("with-ocr", ocr: true);
        await using var _ = catalog;

        var clock = Stopwatch.StartNew();
        await new NumbersStage(catalog).RunAsync(new JobContext(jobId, JobType.AnalyzeInPlace, "motorsport"), null, ct);
        var matching = clock.Elapsed;

        var assets = (await catalog.Jobs.GetItemsAsync(jobId, ct)).Where(i => i.AssetId is not null).Select(i => i.AssetId!.Value).Distinct().ToList();
        var readings = new List<OcrReading>();
        foreach (var asset in assets)
        {
            readings.AddRange(await catalog.Motorsport.GetReadingsAsync(asset, ct));
        }

        var numbers = await catalog.Motorsport.GetJobNumbersAsync(jobId, ct);
        var states = numbers.GroupBy(n => n.State).ToDictionary(g => g.Key, g => g.Count());
        var perPhoto = (withOcr - plain).TotalSeconds / assets.Count;
        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"# Phase 4 performance — {DateTime.Now:yyyy-MM-dd HH:mm}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{files.Count} photos ({custom ?? "motor show 2015, local set"}); profile Motorsport, {Workers} analysis workers (background), faces detection only; after a warm-up.")
            .AppendLine()
            .AppendLine("| Measure | Value |")
            .AppendLine("|---|---|")
            .AppendLine(CultureInfo.InvariantCulture, $"| Analysis without OCR | {plain.TotalSeconds:0.0} s ({files.Count / plain.TotalSeconds:0.00} photos/s) |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Analysis with OCR | {withOcr.TotalSeconds:0.0} s ({files.Count / withOcr.TotalSeconds:0.00} photos/s) |")
            .AppendLine(CultureInfo.InvariantCulture, $"| OCR cost per photo | {perPhoto * 1000:0} ms |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Vehicles read | {numbers.Count} ({numbers.Count / (double)Math.Max(1, assets.Count):0.0} per photo) |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Texts read | {readings.Count} |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Numbers matched (batch, no list) | {matching.TotalMilliseconds:0} ms |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Certain / to review / none / out of list | {states.GetValueOrDefault(NumberState.Certain)} / {states.GetValueOrDefault(NumberState.ToReview)} / {states.GetValueOrDefault(NumberState.NoNumber)} / {states.GetValueOrDefault(NumberState.OutOfList)} |")
            .AppendLine()
            .AppendLine("## Certain numbers without a list (manual check: are they race numbers?)")
            .AppendLine()
            .AppendLine(string.Join(", ", numbers.Where(n => n.State == NumberState.Certain).Select(n => n.Number).GroupBy(n => n).Select(g => $"{g.Key} ×{g.Count()}")))
            .AppendLine()
            .AppendLine("## Most frequent texts read (manual check of the OCR)")
            .AppendLine()
            .AppendLine(string.Join(", ", readings.Where(r => r.Confidence >= 0.8).GroupBy(r => r.Text, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).Take(40).Select(g => $"\"{g.Key}\" ×{g.Count()}")));

        var reports = Path.Combine(TestData.Root ?? _temp.Path, "lab", "performance");
        Directory.CreateDirectory(reports);
        await File.WriteAllTextAsync(Path.Combine(reports, $"phase4-{DateTime.Now:yyyyMMdd-HHmmss}.md"), report.ToString(), ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());

        Assert.NotEmpty(readings);
        Assert.Equal(before, FolderSnapshot.Take(source)); // the folder is only read
    }

    public void Dispose() => _temp.Dispose();
}
