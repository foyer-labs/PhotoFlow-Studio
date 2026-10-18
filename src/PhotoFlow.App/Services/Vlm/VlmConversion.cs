// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PhotoFlow.Core.Inference;
using PhotoFlow.Core.Paths;
using PhotoFlow.Inference.Models;

namespace PhotoFlow.App.Services.Vlm;

public enum VlmConversionStage
{
    Verify,
    Python,
    Space,
    Environment,
    Packages,
    Convert,
    Done,
}

/// <param name="Fraction">0-1 within the stage, when known.</param>
public sealed record VlmConversionProgress(VlmConversionStage Stage, double? Fraction, string? Detail);

public sealed class VlmConversionException(VlmConversionStage stage, string message, string? logPath) : InvalidOperationException(message)
{
    public VlmConversionStage Stage { get; } = stage;

    public string? LogPath { get; } = logPath;
}

/// <summary>A Python found on the PC.</summary>
public sealed record PythonCommand(string Executable, IReadOnlyList<string> Prefix, Version Version);

/// <summary>Processes the conversion runs (real ones, or fakes in the tests).</summary>
public interface IVlmToolchain
{
    /// <summary>A Python 3.10-3.13 installed on the PC, or null.</summary>
    Task<PythonCommand?> FindPythonAsync(CancellationToken ct);

    /// <summary>Runs a program; every line of its output goes to <paramref name="line"/>; returns the exit code.</summary>
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string> line, CancellationToken ct);
}

/// <summary>
/// Conversion of a manual model on the user's PC: the level D model (phase 5b, decisions U2-U3, D5b-1, D5b-2) or the
/// AdaFace face recognizer (P22, ONNX file instead of the GenAI configuration, <see cref="ModelInfo.ConvertedFile"/>). The original weights the
/// user put in the source folder are verified (SHA-256 of the manifest), then a private Python environment is made with
/// the Python already installed (in the cache folder), the pinned packages are installed, and <c>vlm/convert.py</c>
/// writes the ONNX Runtime GenAI model; only when it succeeds the model is moved in place and marked installed. Every
/// line goes to a log in the logs folder. The weights are only read.
/// </summary>
/// <param name="freeBytes">Free space of the drive of a folder (the real drive by default; tests give their own).</param>
public sealed class VlmConversion(ModelRegistry models, IAppPaths paths, IVlmToolchain toolchain, string toolsDirectory, Func<string, long>? freeBytes = null)
{
    // Measured on the 7B (13 Oct 2026): model 6.5 GB, intermediate files of the export 27 GB, environment 4-5 GB.
    public const long ModelBytesNeeded = 8L * 1024 * 1024 * 1024;
    public const long WorkBytesNeeded = 30L * 1024 * 1024 * 1024;
    public const long EnvironmentBytesNeeded = 6L * 1024 * 1024 * 1024;

    private readonly InternalFileCleaner _cleaner = new(paths);

    private static readonly Regex ProgressLine = new(@"^PF-PROGRESS (\d+)/(\d+) (\S+)", RegexOptions.CultureInvariant);

    public string EnvironmentDirectory => Path.Combine(paths.CacheDirectory, "vlm-env");

    public async Task ConvertAsync(string modelId, IProgress<VlmConversionProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.LogsDirectory);
        var logPath = Path.Combine(paths.LogsDirectory, $"vlm-convert-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");
        await using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string line)
        {
            lock (log)
            {
                log.WriteLine(line);
            }
        }

        VlmConversionException Fail(VlmConversionStage stage, string message)
        {
            Log($"ERROR {stage}: {message}");
            return new VlmConversionException(stage, message, logPath);
        }

        // 1. The original weights.
        var model = models.Get(modelId);
        var source = models.SourceDirectory(modelId);
        progress?.Report(new VlmConversionProgress(VlmConversionStage.Verify, 0, source));
        var total = Math.Max(1, model.DownloadBytes);
        var problems = await models.VerifySourceAsync(modelId, new Progress<long>(done => progress?.Report(new VlmConversionProgress(VlmConversionStage.Verify, (double)done / total, null))), ct)
            .ConfigureAwait(false);
        if (problems.Count > 0)
        {
            throw Fail(VlmConversionStage.Verify, string.Join(", ", problems.Select(p => (p.Missing ? "missing " : "different ") + p.Path)));
        }

        Log($"Weights verified in {source}");

        // 2. Python.
        progress?.Report(new VlmConversionProgress(VlmConversionStage.Python, null, null));
        var python = await toolchain.FindPythonAsync(ct).ConfigureAwait(false) ?? throw Fail(VlmConversionStage.Python, "No Python 3.10-3.13 found.");
        Log($"Python {python.Version} at {python.Executable}");

        // 3. Space: the folders on the same drive add up.
        progress?.Report(new VlmConversionProgress(VlmConversionStage.Space, null, null));
        var work = Path.Combine(paths.TempDirectory, "vlm-work");
        Directory.CreateDirectory(paths.ModelsDirectory);
        Directory.CreateDirectory(paths.CacheDirectory);
        Directory.CreateDirectory(paths.TempDirectory);
        // A VLM needs the measured space of the 7B; another model (a face recognizer) about twice its weights.
        var (modelBytes, workBytes) = model.Function == ModelFunction.Vlm ? (ModelBytesNeeded, WorkBytesNeeded) : (2 * model.DownloadBytes, model.DownloadBytes);
        var needs = new[] { (paths.ModelsDirectory, modelBytes), (paths.TempDirectory, workBytes), (paths.CacheDirectory, File.Exists(VenvPython) ? 0 : EnvironmentBytesNeeded) }
            .GroupBy(n => DriveOf(n.Item1), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Folder: g.First().Item1, Bytes: g.Sum(n => n.Item2)));
        if (needs.FirstOrDefault(n => (freeBytes ?? FreeBytes)(n.Folder) < n.Bytes) is { Folder: not null } lacking)
        {
            throw Fail(VlmConversionStage.Space,
                $"Not enough free space on {DriveOf(lacking.Folder)}: {Math.Max(1, lacking.Bytes / (1024 * 1024 * 1024))} GB needed during the conversion.");
        }

        // 4. The private environment, then the pinned packages (again only when the requirements changed).
        var requirements = Path.Combine(toolsDirectory, "requirements.txt");
        var venvPython = VenvPython;
        if (!File.Exists(venvPython))
        {
            progress?.Report(new VlmConversionProgress(VlmConversionStage.Environment, null, EnvironmentDirectory));
            if (await toolchain.RunAsync(python.Executable, [.. python.Prefix, "-m", "venv", EnvironmentDirectory], Log, ct).ConfigureAwait(false) != 0 || !File.Exists(venvPython))
            {
                throw Fail(VlmConversionStage.Environment, "The Python environment could not be created.");
            }
        }

        var stamp = Path.Combine(EnvironmentDirectory, "photoflow-requirements.sha256");
        var wanted = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(requirements, ct).ConfigureAwait(false)));
        if (!File.Exists(stamp) || (await File.ReadAllTextAsync(stamp, ct).ConfigureAwait(false)).Trim() != wanted)
        {
            progress?.Report(new VlmConversionProgress(VlmConversionStage.Packages, null, null));
            if (await toolchain.RunAsync(venvPython, ["-m", "pip", "install", "--disable-pip-version-check", "-r", requirements], Log, ct).ConfigureAwait(false) != 0)
            {
                throw Fail(VlmConversionStage.Packages, "The packages could not be installed (connection, or a Python version without them).");
            }

            await File.WriteAllTextAsync(stamp, wanted, ct).ConfigureAwait(false);
        }

        // 5. The conversion, into a new folder moved in place at the end.
        var target = models.InstallDirectory(modelId);
        var partial = target + ".partial";
        _cleaner.DeleteInternalDirectory(work); // what a stopped conversion left
        if (Directory.Exists(partial))
        {
            Directory.Move(partial, partial + "-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        }

        string? error = null;
        var done = false;
        void Line(string line)
        {
            Log(line);
            if (ProgressLine.Match(line) is { Success: true } m)
            {
                progress?.Report(new VlmConversionProgress(VlmConversionStage.Convert,
                    double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Value));
            }
            else if (line.StartsWith("PF-ERROR ", StringComparison.Ordinal))
            {
                error = line["PF-ERROR ".Length..];
            }
            else if (line == "PF-DONE")
            {
                done = true;
            }
        }

        progress?.Report(new VlmConversionProgress(VlmConversionStage.Convert, 0, null));
        int exit;
        try
        {
            exit = await toolchain.RunAsync(venvPython, [Path.Combine(toolsDirectory, "convert.py"), "--weights", source, "--output", partial, "--work", work], Line, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            // The intermediate files of the export (~27 GB) never stay, whatever the outcome.
            try
            {
                _cleaner.DeleteInternalDirectory(work);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log($"Work folder not removed: {e.Message}");
            }
        }

        if (exit != 0 || !done || !File.Exists(Path.Combine(partial, model.ConvertedFile)))
        {
            throw Fail(VlmConversionStage.Convert, error ?? $"The conversion stopped (exit code {exit}).");
        }

        if (Directory.Exists(target))
        {
            Directory.Move(target, target + "-replaced-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        }

        Directory.Move(partial, target);
        await models.MarkConvertedAsync(modelId, ct).ConfigureAwait(false);
        Log($"Model ready in {target}");
        progress?.Report(new VlmConversionProgress(VlmConversionStage.Done, 1, target));
    }

    private string VenvPython => OperatingSystem.IsWindows() ? Path.Combine(EnvironmentDirectory, "Scripts", "python.exe") : Path.Combine(EnvironmentDirectory, "bin", "python");

    /// <summary>The private Python environment is there (it can be removed once the model is converted).</summary>
    public bool EnvironmentExists => Directory.Exists(EnvironmentDirectory);

    /// <summary>Removes the private Python environment (internal files only, B1); a new conversion makes it again.</summary>
    public void RemoveEnvironment() => _cleaner.DeleteInternalDirectory(EnvironmentDirectory);

    private static string DriveOf(string folder) => Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder;

    private static long FreeBytes(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return long.MaxValue; // unknown: let the conversion tell
        }
    }
}

/// <summary>The real processes: Python found with the launcher <c>py</c> or on the PATH.</summary>
public sealed class ProcessVlmToolchain : IVlmToolchain
{
    private static readonly (string Executable, string[] Prefix)[] Candidates = OperatingSystem.IsWindows()
        ? [("py", ["-3.12"]), ("py", ["-3.13"]), ("py", ["-3.11"]), ("py", ["-3.10"]), ("python", []), ("python3", [])]
        : [("python3.12", []), ("python3.13", []), ("python3.11", []), ("python3.10", []), ("python3", []), ("python", [])];

    public async Task<PythonCommand?> FindPythonAsync(CancellationToken ct)
    {
        foreach (var (executable, prefix) in Candidates)
        {
            var output = new StringBuilder();
            try
            {
                if (await RunAsync(executable, [.. prefix, "--version"], l => output.AppendLine(l), ct).ConfigureAwait(false) == 0
                    && Regex.Match(output.ToString(), @"Python (\d+)\.(\d+)\.(\d+)", RegexOptions.CultureInvariant) is { Success: true } m)
                {
                    var version = new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
                    if (version.Major == 3 && version.Minor is >= 10 and <= 13)
                    {
                        return new PythonCommand(executable, prefix, version);
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // not installed
            }
        }

        return null;
    }

    public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string> line, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(line);
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} could not start.");
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } data)
            {
                line(data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } data)
            {
                line(data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return process.ExitCode;
    }
}
