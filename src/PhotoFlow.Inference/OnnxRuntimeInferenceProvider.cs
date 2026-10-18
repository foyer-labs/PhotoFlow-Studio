// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using PhotoFlow.Core.Inference;

namespace PhotoFlow.Inference;

/// <param name="GpuRuntimeDirectory">Folder of the CUDA/cuDNN runtime DLLs installed by <c>GpuRuntimeInstaller</c> (Windows).</param>
public sealed record InferenceOptions(string? GpuRuntimeDirectory = null, int CudaDeviceId = 0, bool AllowGpu = true);

/// <summary>
/// ONNX Runtime backends (decision F1-2): CUDA on NVIDIA GPUs, CPU everywhere. TF32 is disabled on CUDA: on the RTX 3090
/// it broke detector parity (scores off by 0.16) with no speed gain. If the CUDA provider cannot start, sessions fall back
/// to the CPU and the reason is kept in <see cref="GpuError"/>.
/// </summary>
public sealed class OnnxRuntimeInferenceProvider : IInferenceProvider
{
    private static readonly string[] RequiredWindowsLibraries = ["cudnn64_9.dll", "cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll"];

    private readonly InferenceOptions _options;
    private volatile string? _gpuError;

    public OnnxRuntimeInferenceProvider(InferenceOptions? options = null)
    {
        _options = options ?? new InferenceOptions();
        var (available, name, memory, reason) = ProbeCuda(_options);
        _gpuError = reason;
        Detected = new HardwareInfo(available ? [ExecutionBackend.Cuda, ExecutionBackend.Cpu] : [ExecutionBackend.Cpu], Environment.ProcessorCount, name, memory);
    }

    public HardwareInfo Detected { get; }

    /// <summary>Why the GPU is not used (missing runtime, no NVIDIA GPU, provider failure), or null.</summary>
    public string? GpuError => _gpuError;

    public Task<IInferenceSession> CreateSessionAsync(ModelDescriptor model, ExecutionTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!File.Exists(model.FilePath))
        {
            throw new FileNotFoundException($"Model file for '{model.Id}' not found.", model.FilePath);
        }

        var cudaAvailable = Detected.AvailableBackends.Contains(ExecutionBackend.Cuda) && _gpuError is null;
        if (target == ExecutionTarget.Gpu && !cudaAvailable)
        {
            throw new NotSupportedException($"No usable GPU backend: {_gpuError ?? "CUDA not available"}.");
        }

        var useCuda = target != ExecutionTarget.Cpu && cudaAvailable;
        return Task.Run<IInferenceSession>(
            () =>
            {
                ct.ThrowIfCancellationRequested();
                if (useCuda)
                {
                    try
                    {
                        return Create(model, ExecutionBackend.Cuda);
                    }
                    catch (OnnxRuntimeException e) when (target == ExecutionTarget.Auto)
                    {
                        _gpuError = "CUDA provider failed to start: " + e.Message;
                    }
                }

                return Create(model, ExecutionBackend.Cpu);
            },
            ct);
    }

    private OnnxRuntimeSession Create(ModelDescriptor model, ExecutionBackend backend)
    {
        using var options = backend == ExecutionBackend.Cuda ? CudaOptions() : new SessionOptions();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        return new OnnxRuntimeSession(model, backend, new InferenceSession(model.FilePath, options));
    }

    private SessionOptions CudaOptions()
    {
        using var cuda = new OrtCUDAProviderOptions();
        cuda.UpdateOptions(new Dictionary<string, string>
        {
            ["device_id"] = _options.CudaDeviceId.ToString(CultureInfo.InvariantCulture),
            ["use_tf32"] = "0",
        });
        return SessionOptions.MakeSessionOptionWithCudaProvider(cuda);
    }

    private static (bool Available, string? Name, long? Memory, string? Reason) ProbeCuda(InferenceOptions options)
    {
        if (!options.AllowGpu)
        {
            return (false, null, null, "GPU disabled in settings");
        }

        var (name, memory) = QueryNvidiaGpu();
        if (name is null)
        {
            return (false, null, null, "No NVIDIA GPU detected");
        }

        if (OperatingSystem.IsWindows())
        {
            var dir = options.GpuRuntimeDirectory;
            if (dir is null || RequiredWindowsLibraries.Any(f => !File.Exists(Path.Combine(dir, f))))
            {
                return (false, name, memory, "GPU runtime (CUDA/cuDNN) not installed");
            }

            AddToSearchPath(dir);
        }

        return (true, name, memory, null);
    }

    /// <summary>The CUDA provider loads cuDNN/cuBLAS with the standard DLL search order, which includes PATH.</summary>
    private static void AddToSearchPath(string directory)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!path.Split(Path.PathSeparator).Contains(directory, StringComparer.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
        }
    }

    /// <summary>Name and memory of the first NVIDIA GPU via nvidia-smi (installed with the driver); null when absent.</summary>
    private static (string? Name, long? MemoryBytes) QueryNvidiaGpu()
    {
        try
        {
            var info = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process is null)
            {
                return (null, null);
            }

            var line = process.StandardOutput.ReadLine();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(line))
            {
                return (null, null);
            }

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            long? memory = parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) ? mib * 1024 * 1024 : null;
            return (parts[0], memory);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (null, null);
        }
    }
}
