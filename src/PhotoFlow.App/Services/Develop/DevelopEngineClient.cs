// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PhotoFlow.Core.Develop;
using PhotoFlow.Core.Paths;

namespace PhotoFlow.App.Services.Develop;

/// <summary>
/// The development engine in its own process (decision S1, same path as <c>PhotoFlow.Vlm</c>): started at the first
/// request, kept up, started again after a crash. The packaged engine is <c>engine/photoflow-engine.exe</c> next to the
/// application; while developing, the Python environment <c>engine/.venv</c> of the repository is used instead.
/// </summary>
public sealed class DevelopEngineClient(IAppPaths paths) : IDevelopEngine, IAsyncDisposable
{
    /// <summary>Overrides the engine executable (tests, development).</summary>
    public const string EngineVariable = "PHOTOFLOW_STUDIO_ENGINE";

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _process;
    private DevelopEngineConnection? _connection;

    public string TempDirectory => Path.Combine(paths.TempDirectory, "develop");

    /// <summary>Executable and arguments of the engine, or null when it is not installed.</summary>
    public static (string File, string[] Arguments)? Locate()
    {
        if (Environment.GetEnvironmentVariable(EngineVariable) is { Length: > 0 } custom && File.Exists(custom))
        {
            return (custom, []);
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "engine", OperatingSystem.IsWindows() ? "photoflow-engine.exe" : "photoflow-engine");
        if (File.Exists(bundled))
        {
            return (bundled, []);
        }

        // Development: the Python environment of engine/ in the repository (folders above the build output).
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var python = Path.Combine(dir.FullName, "engine", ".venv", OperatingSystem.IsWindows() ? "Scripts" : "bin",
                OperatingSystem.IsWindows() ? "python.exe" : "python");
            if (File.Exists(python))
            {
                return (python, ["-m", "denoiser.server"]);
            }
        }

        return null;
    }

    public static bool IsAvailable => Locate() is not null;

    private async Task<DevelopEngineConnection> ConnectAsync(CancellationToken ct)
    {
        if (_connection is { IsClosed: false } && _process is { HasExited: false })
        {
            return _connection;
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is { IsClosed: false } && _process is { HasExited: false })
            {
                return _connection;
            }

            Stop();
            var (file, arguments) = Locate() ?? throw new DevelopEngineException(Localization.Strings.Get("Develop.Engine.Missing"));
            var start = new ProcessStartInfo(file)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            Directory.CreateDirectory(TempDirectory);
            start.Environment["PHOTOFLOW_STUDIO_TEMP"] = TempDirectory;
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            // Neural models of the engine in the data folder of the application, with the others.
            start.Environment["DENOISER_MODELS_DIR"] = Path.Combine(paths.ModelsDirectory, "denoiser");
            var exifTool = Path.Combine(AppContext.BaseDirectory, "tools", "exiftool", OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool");
            if (File.Exists(exifTool))
            {
                start.Environment["DENOISER_EXIFTOOL"] = exifTool;
            }

            var process = Process.Start(start) ?? throw new DevelopEngineException(Localization.Strings.Get("Develop.Engine.Missing"));
            _ = Task.Run(() => DrainErrorsAsync(process));
            var connection = new DevelopEngineConnection(process.StandardInput, process.StandardOutput);
            await connection.WaitReadyAsync(ct).WaitAsync(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            _process = process;
            _connection = connection;
            return connection;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task DrainErrorsAsync(Process process)
    {
        var log = Path.Combine(paths.LogsDirectory, $"engine-{DateTime.Now:yyyyMMdd}.log");
        try
        {
            Directory.CreateDirectory(paths.LogsDirectory);
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                await File.AppendAllTextAsync(log, line + Environment.NewLine).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // log not writable: the messages of the engine are not essential
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task<JsonObject> CallAsync(string command, JsonObject arguments, IProgress<DevelopProgress>? progress, CancellationToken ct)
    {
        var connection = await ConnectAsync(ct).ConfigureAwait(false);
        return await connection.CallAsync(command, arguments, progress, ct).ConfigureAwait(false);
    }

    public async Task<DevelopEngineInfo> HelloAsync(CancellationToken ct)
    {
        var r = await CallAsync("hello", [], null, ct).ConfigureAwait(false);
        var algorithms = (r["algorithms"] as JsonArray ?? []).OfType<JsonObject>().Select(a => new DevelopAlgorithm(
            Json.String(a["key"]), Json.String(a["label"]), Json.String(a["summary"]), Json.String(a["description"]), Json.Bool(a["joint"]),
            Json.Bool(a["available"]), Json.Int(a["size_mb"]), Json.String(a["license"]))).ToList();
        var presets = (r["presets"] as JsonArray ?? []).OfType<JsonObject>().Select(p => (Json.String(p["name"]), Json.String(p["description"]))).ToList();
        var gpu = r["gpu"] as JsonObject;
        return new DevelopEngineInfo(Json.String(r["version"]), algorithms, presets, Json.Bool(gpu?["available"]), Json.String(gpu?["text"]),
            r["defaults"]?.DeepClone() as JsonObject ?? []);
    }

    public async Task<DevelopPhoto> OpenAsync(string path, JsonObject? baseSettings, CancellationToken ct)
    {
        var args = new JsonObject { ["path"] = path };
        if (baseSettings is not null)
        {
            args["base"] = baseSettings.DeepClone();
        }

        var r = await CallAsync("open", args, null, ct).ConfigureAwait(false);
        var noise = r["noise"] as JsonObject;
        var wb = new Dictionary<string, (double, double)>();
        foreach (var (mode, value) in r["wb_equivalents"] as JsonObject ?? [])
        {
            if (value is JsonArray { Count: 2 } pair)
            {
                wb[mode] = (Json.Double(pair[0]), Json.Double(pair[1]));
            }
        }

        return new DevelopPhoto(path, Json.Int(r["width"]), Json.Int(r["height"]), Json.Bool(r["is_raw"]), Json.NullableString(r["camera"]),
            Json.NullableDouble(r["iso"]), Json.String(noise?["level"]), Json.String(noise?["detail"]),
            (r["notes"] as JsonArray ?? []).Select(n => Json.String(n)).ToList(), wb, r["settings"]?.DeepClone() as JsonObject ?? [],
            Json.Bool(r["has_settings"]), Json.String(r["suggested_preset"], "Bilanciato"));
    }

    public async Task CloseAsync(string path, CancellationToken ct)
    {
        if (_connection is { IsClosed: false })
        {
            await CallAsync("close", new JsonObject { ["path"] = path }, null, ct).ConfigureAwait(false);
        }
    }

    public async Task<DevelopRender> RenderAsync(string path, JsonObject settings, (int X, int Y, int Width, int Height)? region, int maxSide, bool denoise,
        IProgress<DevelopProgress>? progress, CancellationToken ct)
    {
        var args = new JsonObject { ["path"] = path, ["settings"] = settings.DeepClone(), ["max_side"] = maxSide, ["denoise"] = denoise };
        if (region is { } r)
        {
            args["region"] = new JsonArray(r.X, r.Y, r.Width, r.Height);
        }

        return ToRender(await CallAsync("render", args, progress, ct).ConfigureAwait(false));
    }

    public async Task<DevelopRender> RenderFullAsync(string path, JsonObject settings, int maxSide, IProgress<DevelopProgress>? progress, CancellationToken ct) =>
        ToRender(await CallAsync("render_full", new JsonObject { ["path"] = path, ["settings"] = settings.DeepClone(), ["max_side"] = maxSide }, progress, ct)
            .ConfigureAwait(false));

    public async Task<(DevelopRender Render, JsonObject Settings)> PrepareAsync(string path, JsonObject settings, int maxSide, bool auto,
        IProgress<DevelopProgress>? progress, CancellationToken ct)
    {
        var r = await CallAsync("prepare", new JsonObject { ["path"] = path, ["settings"] = settings.DeepClone(), ["max_side"] = maxSide, ["auto"] = auto }, progress, ct)
            .ConfigureAwait(false);
        return (ToRender(r), r["settings"]?.DeepClone() as JsonObject ?? settings);
    }

    private static DevelopRender ToRender(JsonObject r)
    {
        var region = r["region"] as JsonArray ?? [];
        var frame = r["frame"] as JsonArray ?? [];
        return new DevelopRender(Json.String(r["before"]), Json.NullableString(r["after"]), Json.NullableString(r["difference"]), Json.Int(r["width"]),
            Json.Int(r["height"]), region.Count == 4 ? (Json.Int(region[0]), Json.Int(region[1]), Json.Int(region[2]), Json.Int(region[3])) : (0, 0, 0, 0),
            r["reduction"] is null ? null : Json.Int(r["reduction"]),
            frame.Count == 2 ? (Json.Int(frame[0]), Json.Int(frame[1])) : default);
    }

    public async Task<int[][]> HistogramAsync(string path, JsonObject settings, CancellationToken ct)
    {
        var r = await CallAsync("histogram", new JsonObject { ["path"] = path, ["settings"] = settings.DeepClone() }, null, ct).ConfigureAwait(false);
        return (r["histogram"] as JsonArray ?? []).Select(row => (row as JsonArray ?? []).Select(v => Json.Int(v)).ToArray()).ToArray();
    }

    public async Task<(double Temperature, double Tint)> PickWhiteBalanceAsync(string path, double x, double y, JsonObject settings, CancellationToken ct)
    {
        var r = await CallAsync("pick_wb", new JsonObject { ["path"] = path, ["x"] = x, ["y"] = y, ["settings"] = settings.DeepClone() }, null, ct)
            .ConfigureAwait(false);
        return (Json.Double(r["temperature"]), Json.Double(r["tint"]));
    }

    public async Task<double?> AutoLevelAsync(string path, JsonObject settings, CancellationToken ct)
    {
        var r = await CallAsync("auto_level", new JsonObject { ["path"] = path, ["settings"] = settings.DeepClone() }, null, ct).ConfigureAwait(false);
        return Json.NullableDouble(r["angle"]);
    }

    public async Task<JsonObject> ApplyPresetAsync(string name, JsonObject settings, CancellationToken ct)
    {
        var r = await CallAsync("apply_preset", new JsonObject { ["name"] = name, ["settings"] = settings.DeepClone() }, null, ct).ConfigureAwait(false);
        return r["settings"]?.DeepClone() as JsonObject ?? settings;
    }

    public async Task<IReadOnlyList<DevelopExportResult>> ExportAsync(IReadOnlyList<DevelopExportItem> items, JsonObject output, string policy, string nonRawFormat,
        IProgress<DevelopProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = new JsonArray();
        foreach (var item in items)
        {
            var values = new JsonObject();
            foreach (var (key, value) in item.NameValues)
            {
                values[key] = value;
            }

            var entry = new JsonObject { ["path"] = item.Path, ["settings"] = item.Settings.DeepClone(), ["name_values"] = values, ["auto"] = item.Auto };
            if (item.Metadata is { Count: > 0 } metadata)
            {
                entry["metadata"] = metadata.DeepClone();
            }

            list.Add(entry);
        }

        var r = await CallAsync("export", new JsonObject
        {
            ["items"] = list, ["output"] = output.DeepClone(), ["policy"] = policy, ["non_raw_format"] = nonRawFormat,
        }, progress, ct).ConfigureAwait(false);
        return (r["results"] as JsonArray ?? []).OfType<JsonObject>().Select(x => new DevelopExportResult(Json.String(x["path"]), Json.String(x["outcome"]),
            Json.NullableString(x["output"]), (x["notes"] as JsonArray ?? []).Select(n => Json.String(n)).ToList())).ToList();
    }

    public Task DownloadModelAsync(string key, IProgress<DevelopProgress>? progress, CancellationToken ct) =>
        CallAsync("download_model", new JsonObject { ["key"] = key }, progress, ct);

    private void Stop()
    {
        _connection?.Dispose();
        _connection = null;
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.WriteLine("{\"cmd\":\"quit\"}");
                    process.StandardInput.Flush();
                    if (!process.WaitForExit(3000))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }

            process.Dispose();
            _process = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        _startGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
