// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PhotoFlow.Metadata.ExifTool;

/// <summary>
/// A long-running exiftool process (<c>-stay_open</c>), shared and serialized. Each command ends with a numbered
/// marker on both stdout and stderr, so responses (including binary previews) are read exactly. If the process
/// dies or a command exceeds the timeout, the process is restarted (risk R13).
/// </summary>
public sealed class ExifToolSession : IAsyncDisposable
{
    private readonly ExifToolOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Task<string>? _stderrPump;
    private StringBuilder _stderr = new();
    private int _sequence;

    public ExifToolSession(ExifToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public string ExecutablePath => _options.ExecutablePath;

    /// <summary>Runs a command and returns its text output (UTF-8) and errors.</summary>
    public async Task<ExifToolResponse> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var (stdout, stderr) = await ExecuteRawAsync(arguments, ct).ConfigureAwait(false);
        return new ExifToolResponse(Encoding.UTF8.GetString(stdout), stderr);
    }

    /// <summary>Runs a command whose stdout is binary (e.g. <c>-b -JpgFromRaw</c>).</summary>
    public Task<(byte[] Stdout, string Stderr)> ExecuteBinaryAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        ExecuteRawAsync(arguments, ct);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<(byte[] Stdout, string Stderr)> ExecuteRawAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(a => a.Contains('\n', StringComparison.Ordinal)))
        {
            throw new ArgumentException("exiftool arguments cannot contain line breaks.", nameof(arguments));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                EnsureStarted();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_options.CommandTimeout);
                try
                {
                    return await SendAsync(arguments, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (attempt < 2 && !ct.IsCancellationRequested && e is OperationCanceledException or IOException or InvalidOperationException)
                {
                    // Hung or dead process: restart it once and retry the command (R13).
                    await StopAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(byte[] Stdout, string Stderr)> SendAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var process = _process!;
        var id = Interlocked.Increment(ref _sequence);
        var marker = "{ready" + id.ToString(CultureInfo.InvariantCulture) + "}";
        var errorMarker = "{done" + id.ToString(CultureInfo.InvariantCulture) + "}";

        var command = new StringBuilder();
        command.Append("-charset\nfilename=utf8\n");
        foreach (var argument in arguments)
        {
            command.Append(argument).Append('\n');
        }

        command.Append("-echo4\n").Append(errorMarker).Append('\n');
        command.Append("-execute").Append(id.ToString(CultureInfo.InvariantCulture)).Append('\n');
        var bytes = Encoding.UTF8.GetBytes(command.ToString());
        await process.StandardInput.BaseStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);

        var stdout = await ReadUntilMarkerAsync(process.StandardOutput.BaseStream, Encoding.ASCII.GetBytes(marker), ct).ConfigureAwait(false);
        var stderr = await WaitForStderrMarkerAsync(errorMarker, ct).ConfigureAwait(false);
        return (stdout, stderr);
    }

    private static async Task<byte[]> ReadUntilMarkerAsync(Stream stream, byte[] marker, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("exiftool terminated unexpectedly.");
            }

            buffer.Write(chunk, 0, read);
            var data = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
            var trimmed = data.TrimEnd("\r\n"u8);
            if (trimmed.EndsWith(marker))
            {
                return trimmed[..^marker.Length].ToArray();
            }
        }
    }

    private async Task<string> WaitForStderrMarkerAsync(string marker, CancellationToken ct)
    {
        while (true)
        {
            lock (_stderr)
            {
                var text = _stderr.ToString();
                var index = text.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0)
                {
                    _stderr = new StringBuilder(text[(index + marker.Length)..].TrimStart('\r', '\n'));
                    return text[..index].Trim();
                }
            }

            if (_process is null || _process.HasExited)
            {
                throw new IOException("exiftool terminated unexpectedly.");
            }

            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    private void EnsureStarted()
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        if (!File.Exists(_options.ExecutablePath))
        {
            throw new FileNotFoundException("exiftool not found.", _options.ExecutablePath);
        }

        var isPerlScript = !_options.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(isPerlScript ? "perl" : _options.ExecutablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (isPerlScript)
        {
            info.ArgumentList.Add(_options.ExecutablePath);
        }

        foreach (var argument in new[] { "-stay_open", "True", "-@", "-" })
        {
            info.ArgumentList.Add(argument);
        }

        _stderr = new StringBuilder();
        _process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start exiftool.");
        var process = _process;
        _stderrPump = Task.Run(async () =>
        {
            var buffer = new char[4096];
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (_stderr)
                {
                    _stderr.Append(buffer, 0, read);
                }
            }

            return string.Empty;
        });
    }

    private async Task StopAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteAsync("-stay_open\nFalse\n").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            if (_stderrPump is not null)
            {
                await Task.WhenAny(_stderrPump, Task.Delay(1000)).ConfigureAwait(false);
            }

            process.Dispose();
        }
    }
}

public sealed record ExifToolResponse(string Output, string Errors);

public sealed record ExifToolOptions(string ExecutablePath)
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Keep a <c>_original</c> copy when embedding XMP in a non-RAW file (spec §4, prudent default).</summary>
    public bool KeepOriginalBackup { get; init; } = true;

    /// <summary>exiftool shipped next to the application (tools/exiftool), see build/ExifTool.targets.</summary>
    public static ExifToolOptions Bundled(string applicationDirectory)
    {
        var folder = Path.Combine(applicationDirectory, "tools", "exiftool");
        var windows = Path.Combine(folder, "exiftool.exe");
        return new ExifToolOptions(File.Exists(windows) ? windows : Path.Combine(folder, "exiftool"));
    }
}
