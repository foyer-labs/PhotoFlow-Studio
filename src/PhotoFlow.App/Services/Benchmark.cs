// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using PhotoFlow.Analysis.Culling;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Sources.Scanning;

namespace PhotoFlow.App.Services;

/// <summary>A measured stage of the benchmark: mean time per photo and peak memory while it ran.</summary>
/// <param name="Key">Preview, Culling, Faces, Embedding.</param>
/// <param name="MsPerPhoto">Null when the model of the stage is not installed.</param>
/// <param name="PeakRamBytes">Peak working set of the process during the stage.</param>
/// <param name="PeakVramBytes">Peak GPU memory in use (NVIDIA, from nvidia-smi), null when not measurable.</param>
public sealed record BenchmarkStage(string Key, double? MsPerPhoto, long PeakRamBytes, long? PeakVramBytes);

/// <summary>
/// Result of the in-app benchmark (board 5f, spec §5): the stages one photo at a time, then the whole analysis with
/// the import's parallelism, from which the import and archive times are estimated (copy excluded).
/// </summary>
public sealed record BenchmarkResult(DateTimeOffset At, int Photos, string Backend, IReadOnlyList<BenchmarkStage> Stages, double ParallelMsPerPhoto, int Workers)
{
    /// <summary>Analysis of an import of <paramref name="photos"/> shots.</summary>
    public TimeSpan EstimateImport(int photos) => TimeSpan.FromMilliseconds(ParallelMsPerPhoto * photos);

    /// <summary>Archive analysis runs at low priority with half the workers (spec §10).</summary>
    public TimeSpan EstimateArchive(int photos) =>
        TimeSpan.FromMilliseconds(ParallelMsPerPhoto * photos * Workers / Math.Max(1.0, Math.Max(1, Workers / 2)));
}

/// <summary>
/// Runs the benchmark on about 50 photos of the libraries (read only: nothing is written to the photos, the catalog or
/// the thumbnails). Uses the models already loaded by the pipeline.
/// </summary>
public sealed class BenchmarkRunner(PhotoFlowHost host)
{
    public const int DefaultPhotos = 50;
    public static readonly string[] StageKeys = ["Preview", "Culling", "Faces", "Embedding", "Tags", "Captions", "VlmCaptions"];

    /// <exception cref="InvalidOperationException">No library with photos.</exception>
    public async Task<BenchmarkResult> RunAsync(int photos, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var sample = await SampleAsync(photos, ct).ConfigureAwait(false);
        if (sample.Count == 0)
        {
            throw new InvalidOperationException("No photo in the libraries.");
        }

        // The models stay loaded while measured: a level D pass waits (and is measured at the end on its own).
        using var turn = await host.EnterAnalysisAsync(ct).ConfigureAwait(false);
        var components = await host.GetAnalysisComponentsAsync(ct).ConfigureAwait(false);
        var profile = host.Profiles.Get("events") with { Faces = FaceMode.Off };
        var signalsOnly = new CullingAnalyzer(components.Operations, components.Objects, null);
        var total = (sample.Count * 8) + 1;
        var done = 0;
        void Step()
        {
            done++;
            progress?.Report((done, total));
        }

        // Warm-up: the first inference of each model includes its initialization.
        var warm = await host.Previews.GetPreviewAsync(sample[0], CullingSignals.ReferenceLongSide, ct).ConfigureAwait(false);
        await signalsOnly.AnalyzeDetailedAsync(new PhotoContext(null, sample[0], warm, null), profile, ct).ConfigureAwait(false);
        if (components.Faces is { } warmFaces)
        {
            await warmFaces.DetectAsync(warm.Image, ct).ConfigureAwait(false);
        }

        if (components.Embedder is { } warmEmbedder)
        {
            await warmEmbedder.EmbedAsync(warm.Image, ct).ConfigureAwait(false);
        }

        if (components.Captioner is { } warmCaptioner)
        {
            await warmCaptioner.CaptionAsync(warm.Image, ct).ConfigureAwait(false);
        }

        Step();
        var previews = new List<PreviewImage>(sample.Count);
        var stages = new List<BenchmarkStage>
        {
            await MeasureAsync("Preview", async () =>
            {
                foreach (var path in sample)
                {
                    previews.Add(await host.Previews.GetPreviewAsync(path, CullingSignals.ReferenceLongSide, ct).ConfigureAwait(false));
                    Step();
                }
            }).ConfigureAwait(false),
            await MeasureAsync("Culling", async () =>
            {
                for (var i = 0; i < sample.Count; i++)
                {
                    await signalsOnly.AnalyzeDetailedAsync(new PhotoContext(null, sample[i], previews[i], null), profile, ct).ConfigureAwait(false);
                    Step();
                }
            }).ConfigureAwait(false),
        };

        stages.Add(components.Faces is { } faces
            ? await MeasureAsync("Faces", async () =>
            {
                foreach (var preview in previews)
                {
                    foreach (var face in await faces.DetectAsync(preview.Image, ct).ConfigureAwait(false))
                    {
                        if (components.Fingerprints is { } fingerprints)
                        {
                            await fingerprints.EmbedAsync(preview.Image, face, ct).ConfigureAwait(false);
                        }
                    }

                    Step();
                }
            }).ConfigureAwait(false)
            : Skipped("Faces"));
        stages.Add(components.Embedder is { } embedder
            ? await MeasureAsync("Embedding", async () =>
            {
                foreach (var preview in previews)
                {
                    await embedder.EmbedAsync(preview.Image, ct).ConfigureAwait(false);
                    Step();
                }
            }).ConfigureAwait(false)
            : Skipped("Embedding"));
        stages.Add(components.Tagger is { } tagger
            ? await MeasureAsync("Tags", async () =>
            {
                foreach (var preview in previews)
                {
                    await tagger.TagAsync(preview.Image, ct).ConfigureAwait(false);
                    Step();
                }
            }).ConfigureAwait(false)
            : Skipped("Tags"));
        stages.Add(components.Captioner is { } captioner
            ? await MeasureAsync("Captions", async () =>
            {
                foreach (var preview in previews)
                {
                    await captioner.CaptionAsync(preview.Image, ct).ConfigureAwait(false);
                    Step();
                }
            }).ConfigureAwait(false)
            : Skipped("Captions"));
        previews.Clear();

        // The whole analysis with the import's parallelism (no thumbnail, nothing saved).
        var workers = PipelineDefaults.DefaultAnalysisWorkers;
        var processor = new CullingPhotoProcessor(host.Previews, new CullingAnalyzer(components.Operations, components.Objects, components.Faces), new DecisionEngine(),
            host.Profiles.Get, null, components.Embedder, components.Fingerprints, () => host.FingerprintKeys.Protector);
        var clock = Stopwatch.StartNew();
        await Parallel.ForEachAsync(sample, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, async (path, token) =>
        {
            await processor.ProcessAsync(new PhotoInput(path, null, null), new ProcessingOptions("events", FaceMode.Recognize, Tagger: true), token).ConfigureAwait(false);
            lock (stages)
            {
                Step();
            }
        }).ConfigureAwait(false);
        var parallel = clock.Elapsed.TotalMilliseconds / sample.Count;

        // Level D (phase 5b) as the jobs run it: the other models released, the VLM alone; after a warm-up photo.
        turn.Dispose();
        BenchmarkStage? vlmStage = null;
        await host.Vlm.RunAsync(async captioner =>
        {
            await captioner.CaptionAsync((await host.Previews.GetPreviewAsync(sample[0], CullingSignals.ReferenceLongSide, ct).ConfigureAwait(false)).Image, ct)
                .ConfigureAwait(false);
            vlmStage = await MeasureAsync("VlmCaptions", async () =>
            {
                foreach (var path in sample)
                {
                    var preview = await host.Previews.GetPreviewAsync(path, CullingSignals.ReferenceLongSide, ct).ConfigureAwait(false);
                    await captioner.CaptionAsync(preview.Image, ct).ConfigureAwait(false);
                    Step();
                }
            }).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        stages.Add(vlmStage ?? Skipped("VlmCaptions"));

        var backend = host.DescribeAnalysis().Backend.ToString();
        return new BenchmarkResult(DateTimeOffset.UtcNow, sample.Count, backend, stages, parallel, workers);

        BenchmarkStage Skipped(string key)
        {
            for (var i = 0; i < sample.Count; i++)
            {
                Step();
            }

            return new BenchmarkStage(key, null, 0, null);
        }

        async Task<BenchmarkStage> MeasureAsync(string key, Func<Task> body)
        {
            using var sampler = new MemorySampler(host.Inference.Detected.GpuName is { } gpu && gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
            var watch = Stopwatch.StartNew();
            await body().ConfigureAwait(false);
            watch.Stop();
            var (ram, vram) = await sampler.StopAsync().ConfigureAwait(false);
            return new BenchmarkStage(key, watch.Elapsed.TotalMilliseconds / sample.Count, ram, vram);
        }
    }

    /// <summary>About <paramref name="count"/> analyzable photos spread over the libraries (one per pair of RAW+JPEG).</summary>
    private async Task<List<string>> SampleAsync(int count, CancellationToken ct)
    {
        var candidates = new List<string>();
        foreach (var library in await host.Catalog.Libraries.ListAsync(ct).ConfigureAwait(false))
        {
            if (!Directory.Exists(library.RootPath))
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await foreach (var file in new SourceScanner(host.FileSystem, library.RootPath).ScanAsync(ct).ConfigureAwait(false))
            {
                if (file.IsAnalyzable && seen.Add(file.PairKey) && !file.Item.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.StartsWith('_')))
                {
                    candidates.Add(file.Item.FullPath);
                }

                if (candidates.Count >= count * 40)
                {
                    break;
                }
            }
        }

        if (candidates.Count <= count)
        {
            return candidates;
        }

        var step = (double)candidates.Count / count;
        return [.. Enumerable.Range(0, count).Select(i => candidates[(int)(i * step)])];
    }

    /// <summary>Peak working set of the process and, with an NVIDIA GPU, peak GPU memory in use, sampled every 100 ms.</summary>
    private sealed class MemorySampler : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<(long Ram, long? Vram)> _task;

        public MemorySampler(bool nvidia) => _task = Task.Run(() => SampleAsync(nvidia, _stop.Token));

        public async Task<(long Ram, long? Vram)> StopAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            return await _task.ConfigureAwait(false);
        }

        public void Dispose() => _stop.Dispose();

        private static async Task<(long Ram, long? Vram)> SampleAsync(bool nvidia, CancellationToken ct)
        {
            long ram = 0;
            long? vram = null;
            using var process = Process.GetCurrentProcess();
            while (true)
            {
                process.Refresh();
                ram = Math.Max(ram, process.WorkingSet64);
                if (nvidia && await GpuMemoryUsedAsync().ConfigureAwait(false) is { } used)
                {
                    vram = Math.Max(vram ?? 0, used);
                }

                try
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return (ram, vram);
                }
            }
        }

        /// <summary>GPU memory in use (all processes), from the NVIDIA driver tool; null when not available.</summary>
        private static async Task<long?> GpuMemoryUsedAsync()
        {
            try
            {
                using var smi = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (smi is null)
                {
                    return null;
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                string output;
                try
                {
                    output = await smi.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                    await smi.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    smi.Kill();
                    return null;
                }

                return long.TryParse(output.Split('\n')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) ? mib * 1024 * 1024 : null;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or IOException)
            {
                return null;
            }
        }
    }
}
