// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Paths;
using PhotoFlow.Core.Tagging;

namespace PhotoFlow.App.Services.Vlm;

/// <summary>
/// Level D captions (phase 5b): Qwen2.5-VL-7B in the <c>PhotoFlow.Vlm</c> process (ONNX Runtime GenAI, exception U4).
/// The process starts at the first caption and stays up; each photo goes to it as a JPEG of at most
/// <see cref="MaxSide"/> pixels in the temporary folder (removed after). Italian captions (U5), greedy, short (D5b-5).
/// A process that closes is started again at the next caption. Its errors (a model that does not load, not enough GPU
/// memory) go to <c>logs/vlm-yyyyMMdd.log</c>.
/// </summary>
public sealed class VlmCaptioner(string modelDirectory, string modelVersion, string? gpuRuntimeDirectory, IImageOperations imageOperations, IAppPaths paths)
    : IImageCaptioner, IAsyncDisposable
{
    public const int MaxSide = 1024;
    public const int MaxTokens = 128;
    public const string Prompt = "Descrivi questa foto in italiano in una o due frasi, in modo oggettivo, senza inventare nomi di persone o luoghi.";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly InternalFileCleaner _cleaner = new(paths);
    private Process? _process;
    private VlmClient? _client;

    public string ModelVersion => modelVersion;

    public string Language => "it";

    /// <summary>The helper next to the application.</summary>
    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "PhotoFlow.Vlm.exe" : "PhotoFlow.Vlm");

    public async Task<string> CaptionAsync(RgbImage image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(image.Width, image.Height));
        var input = scale < 1
            ? imageOperations.Resize(image, Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)), ResampleMode.Bilinear)
            : image;
        var folder = Path.Combine(paths.TempDirectory, "vlm");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".jpg");
        await File.WriteAllBytesAsync(file, imageOperations.EncodeJpeg(input, 92), ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = await StartAsync(ct).ConfigureAwait(false);
            return await client.CaptionAsync(file, Prompt, MaxTokens, ct).ConfigureAwait(false);
        }
        catch (VlmException e)
        {
            Stop(); // started again at the next caption
            Log(e.Message);
            throw;
        }
        finally
        {
            _gate.Release();
            _cleaner.DeleteInternalFile(file);
        }
    }

    private async Task<VlmClient> StartAsync(CancellationToken ct)
    {
        if (_client is not null && _process is { HasExited: false })
        {
            return _client;
        }

        Stop();
        var start = new ProcessStartInfo(HelperPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(modelDirectory);
        if (gpuRuntimeDirectory is not null && Directory.Exists(gpuRuntimeDirectory))
        {
            // The CUDA and cuDNN libraries of the GPU pack, as for the application's own sessions.
            start.Environment["PATH"] = gpuRuntimeDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }

        _process = Process.Start(start) ?? throw new VlmException("The captions process could not start.");
        _process.ErrorDataReceived += (_, _) => { }; // drained: native logs are not needed
        _process.BeginErrorReadLine();
        _client = new VlmClient(_process.StandardInput, _process.StandardOutput);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        await _client.WaitReadyAsync(timeout.Token).ConfigureAwait(false);
        return _client;
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(paths.LogsDirectory);
            File.AppendAllText(Path.Combine(paths.LogsDirectory, $"vlm-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {modelVersion}: {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the caption failure is reported anyway
        }
    }

    private void Stop()
    {
        _client?.Dispose();
        _client = null;
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.WriteLine("quit");
                    if (!process.WaitForExit(5000))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                // already gone
            }

            process.Dispose();
            _process = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
