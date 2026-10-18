// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.IO.Hashing;
using PhotoFlow.Core.Ingest;
using PhotoFlow.Core.IO;

namespace PhotoFlow.Sources.Ingest;

/// <summary>
/// Verified copy (spec §4): a single read of the source feeds every local destination in parallel while the
/// xxHash3 of the source is computed; each copy is written to a <c>.pftmp</c> file, verified by re-reading it,
/// then renamed to its final name. Existing files are never overwritten and nothing is ever deleted (I4):
/// a leftover <c>.pftmp</c> is simply overwritten by the next attempt.
/// </summary>
public sealed class CopyEngine : ICopyEngine
{
    public const string TempExtension = ".pftmp";
    private const int ChunkSize = 1 << 20;

    private readonly IFileSystem _fileSystem;
    private readonly int _maxAttempts;

    public CopyEngine(IFileSystem fileSystem, int maxAttempts = 2)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        _fileSystem = fileSystem;
        _maxAttempts = maxAttempts;
    }

    public async Task<CopyResult> CopyAsync(IngestFile file, IReadOnlyList<IngestDestination> destinations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destinations);
        var primary = destinations.Single(d => d.Kind == DestinationKind.Primary);
        var results = new Dictionary<string, DestinationResult>();
        foreach (var deferred in destinations.Where(d => d.Kind == DestinationKind.Deferred))
        {
            results[deferred.Id] = new DestinationResult(deferred.Id, CopyOutcome.Queued, null, null);
        }

        // 1. Resolve the target of each local destination.
        var targets = new List<Target>();
        foreach (var destination in destinations.Where(d => d.Kind != DestinationKind.Deferred))
        {
            var final = Path.Combine(destination.RootPath, file.TargetRelativePath);
            try
            {
                _fileSystem.CreateDirectory(Path.GetDirectoryName(final)!);
                targets.Add(new Target(destination, final));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                results[destination.Id] = Failure(destination, e);
            }
        }

        // 2. Single read of the source: hash it and write every target that does not exist yet.
        var toWrite = targets.Where(t => !_fileSystem.FileExists(t.FinalPath)).ToList();
        var (sourceHash, sourceError) = await CopyPassAsync(file.SourcePath, toWrite, ct).ConfigureAwait(false);
        if (sourceError is not null)
        {
            foreach (var target in targets)
            {
                results[target.Destination.Id] = new DestinationResult(target.Destination.Id, CopyOutcome.Failed, null, "Source read error: " + sourceError);
            }

            return Result(file, null, primary, destinations, results, sourceError);
        }

        if (file.ExpectedHash is { } expected && sourceHash != expected)
        {
            // The source is not the file that was verified earlier: nothing is finalized (temporary files are left to be overwritten).
            const string mismatch = "The source no longer matches its verified hash.";
            foreach (var target in targets)
            {
                results[target.Destination.Id] = new DestinationResult(target.Destination.Id, CopyOutcome.Failed, null, mismatch);
            }

            return Result(file, sourceHash, primary, destinations, results, mismatch);
        }

        // 3. Existing files: identical means already present; different means a new, non-colliding name.
        var collisions = new List<Target>();
        foreach (var target in targets.Except(toWrite))
        {
            var existing = await TryHashAsync(target.FinalPath, ct).ConfigureAwait(false);
            if (existing == sourceHash)
            {
                results[target.Destination.Id] = new DestinationResult(target.Destination.Id, CopyOutcome.AlreadyPresent, target.FinalPath, null);
            }
            else
            {
                collisions.Add(target.WithFinalPath(FreeName(target.FinalPath)));
            }
        }

        // 4. Verify every written copy by re-reading it; retry with a new read of the source if needed.
        var pending = toWrite.Concat(collisions).Where(t => !results.ContainsKey(t.Destination.Id)).ToList();
        if (collisions.Count > 0)
        {
            await RewriteAsync(file.SourcePath, collisions, sourceHash!.Value, ct).ConfigureAwait(false);
        }

        for (var attempt = 1; pending.Count > 0; attempt++)
        {
            var retry = new List<Target>();
            foreach (var target in pending)
            {
                if (target.WriteError is { } writeError)
                {
                    results[target.Destination.Id] = Failure(target.Destination, writeError);
                    continue;
                }

                var copied = await TryHashAsync(target.TempPath, ct).ConfigureAwait(false);
                if (copied == sourceHash)
                {
                    results[target.Destination.Id] = Finalize(target);
                }
                else if (attempt < _maxAttempts)
                {
                    retry.Add(target);
                }
                else
                {
                    results[target.Destination.Id] = new DestinationResult(
                        target.Destination.Id, CopyOutcome.Failed, null, "Verification failed: the copy differs from the source.");
                }
            }

            if (retry.Count > 0)
            {
                await RewriteAsync(file.SourcePath, retry, sourceHash!.Value, ct).ConfigureAwait(false);
            }

            pending = retry;
        }

        return Result(file, sourceHash, primary, destinations, results, null);
    }

    /// <summary>Hash of a file with xxHash3 (64 bit), as used by the catalog for identity.</summary>
    public static async Task<ulong> HashAsync(IFileSystem fileSystem, string path, CancellationToken ct)
    {
        var hasher = new XxHash3();
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            await using var stream = fileSystem.OpenRead(path);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize), ct).ConfigureAwait(false)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, read));
            }

            return hasher.GetCurrentHashAsUInt64();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static CopyResult Result(
        IngestFile file, ulong? hash, IngestDestination primary, IReadOnlyList<IngestDestination> destinations,
        Dictionary<string, DestinationResult> results, string? sourceError) =>
        new(file, hash, primary.Id, destinations.Select(d => results[d.Id]).ToList(), sourceError);

    private static DestinationResult Failure(IngestDestination destination, Exception error) =>
        new(destination.Id, IsUnavailable(error) ? CopyOutcome.DestinationUnavailable : CopyOutcome.Failed, null, error.Message);

    /// <summary>The destination cannot be used at all (disconnected disk, unreachable share, full disk): the job pauses.</summary>
    private static bool IsUnavailable(Exception error) => error switch
    {
        DirectoryNotFoundException or DriveNotFoundException => true,
        IOException io => (io.HResult & 0xFFFF) is 0x15 /* not ready */ or 0x35 /* network path */ or 0x40 /* netname deleted */
            or 0x70 /* disk full */ or 0x27 /* handle disk full */,
        _ => false,
    };

    private async Task<(ulong? Hash, string? Error)> CopyPassAsync(string sourcePath, IReadOnlyList<Target> targets, CancellationToken ct)
    {
        foreach (var target in targets)
        {
            target.WriteError = null;
            try
            {
                target.Stream = _fileSystem.OpenWrite(target.TempPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                target.WriteError = e;
            }
        }

        var hasher = new XxHash3();
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            await using (var source = _fileSystem.OpenRead(sourcePath))
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, ChunkSize), ct).ConfigureAwait(false)) > 0)
                {
                    hasher.Append(buffer.AsSpan(0, read));
                    var chunk = buffer.AsMemory(0, read);
                    await Task.WhenAll(targets.Where(t => t.Stream is not null).Select(t => WriteChunkAsync(t, chunk, ct))).ConfigureAwait(false);
                }
            }

            return (hasher.GetCurrentHashAsUInt64(), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, e.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            foreach (var target in targets)
            {
                await CloseAsync(target).ConfigureAwait(false);
            }
        }
    }

    private async Task RewriteAsync(string sourcePath, IReadOnlyList<Target> targets, ulong expectedHash, CancellationToken ct)
    {
        var (hash, error) = await CopyPassAsync(sourcePath, targets, ct).ConfigureAwait(false);
        if (error is not null || hash != expectedHash)
        {
            var reason = new IOException(error ?? "The source changed while it was being copied.");
            foreach (var target in targets)
            {
                target.WriteError ??= reason;
            }
        }
    }

    private static async Task WriteChunkAsync(Target target, ReadOnlyMemory<byte> chunk, CancellationToken ct)
    {
        try
        {
            await target.Stream!.WriteAsync(chunk, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            target.WriteError = e;
            await CloseAsync(target).ConfigureAwait(false);
        }
    }

    private static async Task CloseAsync(Target target)
    {
        if (target.Stream is null)
        {
            return;
        }

        try
        {
            await target.Stream.FlushAsync().ConfigureAwait(false);
            await target.Stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            target.WriteError ??= e;
        }
        finally
        {
            target.Stream = null;
        }
    }

    private async Task<ulong?> TryHashAsync(string path, CancellationToken ct)
    {
        try
        {
            return await HashAsync(_fileSystem, path, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private DestinationResult Finalize(Target target)
    {
        var final = target.FinalPath;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                _fileSystem.Move(target.TempPath, final);
                return new DestinationResult(target.Destination.Id, CopyOutcome.Verified, final, null);
            }
            catch (IOException) when (_fileSystem.FileExists(final))
            {
                final = FreeName(final); // someone created the name meanwhile: never overwrite
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failure(target.Destination, e);
            }
        }

        return new DestinationResult(target.Destination.Id, CopyOutcome.Failed, null, "No free file name available.");
    }

    /// <summary><c>name.ext</c> → first free <c>name-2.ext</c>, <c>name-3.ext</c>…</summary>
    private string FreeName(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(directory, $"{stem}-{n}{extension}");
            if (!_fileSystem.FileExists(candidate) && !_fileSystem.FileExists(candidate + TempExtension))
            {
                return candidate;
            }
        }
    }

    /// <summary>One destination of one file (reference identity: it carries the open stream and the write error).</summary>
    private sealed class Target(IngestDestination destination, string finalPath)
    {
        public IngestDestination Destination { get; } = destination;

        public string FinalPath { get; } = finalPath;

        public string TempPath => FinalPath + TempExtension;

        public Stream? Stream { get; set; }

        public Exception? WriteError { get; set; }

        public Target WithFinalPath(string path) => new(Destination, path);
    }
}
