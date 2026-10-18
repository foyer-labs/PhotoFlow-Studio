// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using PhotoFlow.App.Services.Vlm;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Tagging;
using PhotoFlow.Data;
using PhotoFlow.Imaging;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tests.Parity;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Quality;

/// <summary>
/// Phase 5b measurements on the local photos, read in place, as the application runs level D: the analysis with every
/// other model (detection, faces with recognition, CLIP, RAM++, Florence-2), then those models released and the level
/// D pass alone on the GPU (<see cref="VlmCaptionStage"/>). Time per photo and peak GPU memory of the whole system
/// (nvidia-smi) for each part: the basis of the GPU requirement (P16). Every photo ends with an Italian and an English
/// caption.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Performance)]
public sealed class Phase5bPerformanceTests : IDisposable
{
    private static readonly int Workers = Math.Max(1, PipelineDefaults.DefaultAnalysisWorkers / 2);

    private readonly TempFolder _temp = new("pf-phase5b-perf");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Level_D_pass_cost_and_gpu_memory_on_the_local_set()
    {
        var source = TestData.RequireDirectory("photos");
        var model = TestData.RequireDirectory("models", "qwen2.5-vl-7b");
        var files = Directory.EnumerateFiles(source, "*.jpg").Order(StringComparer.Ordinal).Take(24).ToList();
        var fs = new PhotoFlow.Core.IO.PhysicalFileSystem();
        var library = _temp.Sub("library");
        foreach (var file in files)
        {
            File.Copy(file, Path.Combine(library, Path.GetFileName(file)));
        }

        var report = new StringBuilder().AppendLine(CultureInfo.InvariantCulture, $"# Phase 5b performance — level D pass — {DateTime.Now:yyyy-MM-dd HH:mm}").AppendLine();
        var idle = GpuMemoryUsedMiB();
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), _temp.Sub("b")), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "L", library, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);
        var tags = TagLevels.Default | TagLevels.Caption | TagLevels.VlmCaption;
        var jobId = await ArchiveJobRunner.CreateAsync(catalog, fs, new ArchiveJobSettings(libraryId, "events", ArchiveOrder.ByFolder, FaceMode.Recognize, Workers, false, tags), Ct);

        // 1. The analysis with every other model.
        TimeSpan analysisTime;
        int analysisPeak;
        await using (var analysis = await LocalAnalysis.CreateAsync(fs, null, Ct, recognition: true, tagging: true, captions: true))
        {
            using var sampler = new PeakSampler();
            var clock = Stopwatch.StartNew();
            await new ArchiveJobRunner(catalog, new ExifToolExifReader(analysis.ExifTool), fs, analysis.Processor).RunAsync(jobId, null, Ct);
            analysisTime = clock.Elapsed;
            analysisPeak = await sampler.StopAsync();
        }

        // 2. The models released, the level D pass alone.
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        await using var exif = ExifToolFixture.Start();
        var previews = new PreviewProvider(fs, ImageHandlerRegistry.CreateDefault(fs, new ExifToolPreviewSource(exif), new MagickRawDecoder()));
        var vlm = new LocalVlm(model, GpuRuntime.Directory, paths);
        TimeSpan passTime;
        int passPeak;
        using (var sampler = new PeakSampler())
        {
            var clock = Stopwatch.StartNew();
            await new VlmCaptionStage(catalog, fs, previews, vlm).RunAsync(new JobContext(jobId, JobType.AnalyzeInPlace, "events"), null, Ct);
            passTime = clock.Elapsed;
            passPeak = await sampler.StopAsync();
        }

        var assets = (await catalog.Jobs.GetItemsAsync(jobId, Ct)).Select(i => i.AssetId).OfType<long>().ToList();
        foreach (var asset in assets)
        {
            Assert.Equal(["it", "en"], (await catalog.Captions.GetAllAsync(asset, Ct)).Select(c => c.Language));
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"{files.Count} photos, {Workers} workers; GPU memory of the system when idle: {idle} MiB.")
            .AppendLine(CultureInfo.InvariantCulture, $"- Analysis (D-FINE, buffalo_l, CLIP, RAM++, Florence-2): {analysisTime.TotalSeconds:0.0} s ({analysisTime.TotalMilliseconds / files.Count:0} ms per photo), peak {analysisPeak} MiB")
            .AppendLine(CultureInfo.InvariantCulture, $"- Level D pass alone (start of the process included): {passTime.TotalSeconds:0.0} s ({passTime.TotalMilliseconds / files.Count:0} ms per photo), peak {passPeak} MiB (+{passPeak - idle} MiB)");
        await File.WriteAllTextAsync(Path.Combine(TestData.RequireDirectory("lab"), "phase5b-vlm.md"), report.ToString(), Ct);
        TestContext.Current.SendDiagnosticMessage(report.ToString());
    }

    public void Dispose() => _temp.Dispose();

    private static int GpuMemoryUsedMiB()
    {
        try
        {
            using var smi = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var text = smi.StandardOutput.ReadToEnd();
            smi.WaitForExit();
            return int.Parse(text.Trim().Split('\n')[0], CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FormatException or InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>The VLM of the test data, started for the pass and closed after (as the application does).</summary>
    private sealed class LocalVlm(string model, string gpuRuntime, IAppPaths paths) : IExclusiveCaptioning
    {
        public string? ModelVersion => "qwen2.5-vl-7b@local";

        public string Language => "it";

        public async Task RunAsync(Func<IImageCaptioner, Task> work, CancellationToken ct)
        {
            await using var captioner = new VlmCaptioner(model, ModelVersion!, gpuRuntime, new SkiaImageOperations(), paths);
            await work(captioner);
        }
    }

    private sealed class PeakSampler : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<int> _task;

        public PeakSampler() => _task = Task.Run(async () =>
        {
            var max = 0;
            while (!_stop.IsCancellationRequested)
            {
                max = Math.Max(max, GpuMemoryUsedMiB());
                await Task.Delay(200, CancellationToken.None);
            }

            return max;
        });

        public async Task<int> StopAsync()
        {
            await _stop.CancelAsync();
            return await _task;
        }

        public void Dispose() => _stop.Dispose();
    }
}
