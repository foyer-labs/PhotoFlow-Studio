// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using PhotoFlow.Analysis.Culling;
using PhotoFlow.Analysis.Profiles;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Data;
using PhotoFlow.Imaging;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Sources.Jobs;

// Usage: PhotoFlow.ScenarioHost <catalog> <backups> <jobId> <processed-log> <delay-ms> [import|archive]
// Recovers interrupted jobs, then runs or resumes the job. Prints "copied N completed N failed N" and finally "state S".
// The processor appends every processed path to the log (flushed per line) and waits <delay-ms> per photo.
// "import" (default) uses a fake analysis; "archive" runs the in-place analysis with the real culling analysis
// (previews through the format handlers, no models), so that formats and damaged files are exercised at scale.
var (catalogPath, backups, jobId, log, delay) = (args[0], args[1], long.Parse(args[2], CultureInfo.InvariantCulture), args[3], int.Parse(args[4], CultureInfo.InvariantCulture));
var archive = args.Length > 5 && args[5] == "archive";

await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(catalogPath, backups), CancellationToken.None);
var interrupted = await ImportJobRunner.RecoverInterruptedAsync(catalog, CancellationToken.None);
Console.WriteLine($"interrupted {interrupted.Count}");

var fs = new PhysicalFileSystem();
IJobRunner runner;
if (archive)
{
    var previews = new PreviewProvider(fs, ImageHandlerRegistry.CreateDefault(fs, new NoEmbeddedPreviews(), new NoRawDecoder()));
    var real = new CullingPhotoProcessor(previews, new CullingAnalyzer(new SkiaImageOperations(), null, null), new DecisionEngine(), BuiltInProfiles.Get, null);
    runner = new ArchiveJobRunner(catalog, new NoExifReader(), fs, new LoggingProcessor(log, delay, real));
}
else
{
    runner = new ImportJobRunner(catalog, new IngestService(new CopyEngine(fs)), new NoExifReader(), fs, new LoggingProcessor(log, delay, null));
}

var progress = new SynchronousProgress(p => Console.WriteLine($"copied {p.Copied} completed {p.Completed} failed {p.Failed}"));
var summary = await runner.RunAsync(jobId, progress, CancellationToken.None);
Console.WriteLine($"state {summary.State}");
return 0;

/// <summary>Reports on the calling thread, in order (Progress&lt;T&gt; would post to the thread pool).</summary>
internal sealed class SynchronousProgress(Action<JobProgress> report) : IProgress<JobProgress>
{
    private readonly object _gate = new();

    public void Report(JobProgress value)
    {
        lock (_gate)
        {
            report(value);
        }
    }
}

internal sealed class NoExifReader : IExifReader
{
    public Task<IReadOnlyList<ExifInfo>> ReadAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExifInfo>>([]);
}

/// <summary>The scenario RAW files are twins of a JPEG (analyzed through it): their own preview is never needed.</summary>
internal sealed class NoEmbeddedPreviews : IEmbeddedPreviewSource
{
    public Task<EmbeddedPreviews> ExtractLargestAsync(string rawPath, CancellationToken ct) => Task.FromResult(new EmbeddedPreviews(null));
}

internal sealed class NoRawDecoder : IRawDecoder
{
    public Task<RgbImage> DecodeAsync(string rawPath, int maxSize, CancellationToken ct) =>
        throw new ImageDecodeException($"{Path.GetFileName(rawPath)}: no RAW decoder in the scenario host.");
}

/// <summary>Logs every path handed to the analysis (successful or not), then returns the inner result or a fake one.</summary>
internal sealed class LoggingProcessor(string log, int delayMs, IPhotoProcessor? inner) : IPhotoProcessor
{
    private readonly object _gate = new();

    public async Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct)
    {
        await Task.Delay(delayMs, ct);
        try
        {
            return inner is null
                ? new PhotoProcessingResult(
                    new AnalyzerResult("scenario", "scenario@1", new Dictionary<string, double>()),
                    [],
                    new DecisionProposal(CullingOutcome.Kept, []),
                    "scenario")
                : await inner.ProcessAsync(photo, options, ct);
        }
        finally
        {
            lock (_gate)
            {
                using var writer = new StreamWriter(log, append: true);
                writer.WriteLine(photo.Path);
            }
        }
    }
}
