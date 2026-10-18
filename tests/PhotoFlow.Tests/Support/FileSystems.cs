// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using PhotoFlow.Core.IO;

namespace PhotoFlow.Tests.Support;

/// <summary>
/// Wraps a file system and refuses (and records) every write, create or move under protected roots.
/// Used to prove that the import source is never written to (invariant I1).
/// </summary>
public sealed class RecordingFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;
    private readonly string[] _protectedRoots;
    private readonly ConcurrentQueue<string> _violations = new();

    public RecordingFileSystem(IFileSystem inner, params string[] protectedRoots)
    {
        _inner = inner;
        _protectedRoots = protectedRoots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).ToArray();
    }

    public IReadOnlyCollection<string> Violations => _violations.ToArray();

    public bool FileExists(string path) => _inner.FileExists(path);

    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption option) =>
        _inner.EnumerateFiles(directory, searchPattern, option);

    public FileMetadata GetFileMetadata(string path) => _inner.GetFileMetadata(path);

    public Stream OpenRead(string path) => _inner.OpenRead(path);

    public Stream OpenWrite(string path)
    {
        Guard("OpenWrite", path);
        return _inner.OpenWrite(path);
    }

    public void CreateDirectory(string path)
    {
        Guard("CreateDirectory", path);
        _inner.CreateDirectory(path);
    }

    public void Move(string sourcePath, string destinationPath)
    {
        Guard("Move (from)", sourcePath);
        Guard("Move (to)", destinationPath);
        _inner.Move(sourcePath, destinationPath);
    }

    public void Replace(string replacementPath, string destinationPath, string? backupPath)
    {
        Guard("Replace (from)", replacementPath);
        Guard("Replace (to)", destinationPath);
        if (backupPath is not null)
        {
            Guard("Replace (backup)", backupPath);
        }

        _inner.Replace(replacementPath, destinationPath, backupPath);
    }

    public long GetAvailableFreeSpace(string path) => _inner.GetAvailableFreeSpace(path);

    private void Guard(string operation, string path)
    {
        var full = Path.GetFullPath(path);
        if (_protectedRoots.Any(root => full.Equals(root, StringComparison.OrdinalIgnoreCase)
                                         || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            _violations.Enqueue($"{operation}: {full}");
            throw new UnauthorizedAccessException($"Test guard: {operation} on protected path {full}");
        }
    }
}

/// <summary>Injects faults: corrupted writes, vanishing destinations, truncated reads.</summary>
public sealed class FaultInjectingFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;

    public FaultInjectingFileSystem(IFileSystem inner) => _inner = inner;

    /// <summary>Writes to matching paths get one byte flipped (simulates a bad medium or cable).</summary>
    public Func<string, bool> CorruptWrites { get; set; } = _ => false;

    /// <summary>Writes and directory creation under matching paths fail as a disconnected device.</summary>
    public Func<string, bool> Unavailable { get; set; } = _ => false;

    /// <summary>Reads of matching paths fail midway (simulates a truncated file or a failing card).</summary>
    public Func<string, bool> FailReads { get; set; } = _ => false;

    public bool FileExists(string path) => _inner.FileExists(path);

    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption option) =>
        _inner.EnumerateFiles(directory, searchPattern, option);

    public FileMetadata GetFileMetadata(string path) => _inner.GetFileMetadata(path);

    public Stream OpenRead(string path) =>
        FailReads(path) ? new FailingReadStream(_inner.OpenRead(path)) : _inner.OpenRead(path);

    public Stream OpenWrite(string path)
    {
        ThrowIfUnavailable(path);
        var stream = _inner.OpenWrite(path);
        return CorruptWrites(path) ? new CorruptingWriteStream(stream) : stream;
    }

    public void CreateDirectory(string path)
    {
        ThrowIfUnavailable(path);
        _inner.CreateDirectory(path);
    }

    public void Move(string sourcePath, string destinationPath)
    {
        ThrowIfUnavailable(destinationPath);
        _inner.Move(sourcePath, destinationPath);
    }

    public void Replace(string replacementPath, string destinationPath, string? backupPath)
    {
        ThrowIfUnavailable(destinationPath);
        _inner.Replace(replacementPath, destinationPath, backupPath);
    }

    public long GetAvailableFreeSpace(string path) => _inner.GetAvailableFreeSpace(path);

    private void ThrowIfUnavailable(string path)
    {
        if (Unavailable(path))
        {
            throw new DirectoryNotFoundException($"Test fault: device not ready for {path}");
        }
    }

    private sealed class CorruptingWriteStream(Stream inner) : DelegatingStream(inner)
    {
        private bool _corrupted;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!_corrupted && buffer.Length > 0)
            {
                var copy = buffer.ToArray();
                copy[copy.Length / 2] ^= 0xFF;
                _corrupted = true;
                Inner.Write(copy);
                return;
            }

            Inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_corrupted && buffer.Length > 0)
            {
                var copy = buffer.ToArray();
                copy[copy.Length / 2] ^= 0xFF;
                _corrupted = true;
                return Inner.WriteAsync(copy, cancellationToken);
            }

            return Inner.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class FailingReadStream(Stream inner) : DelegatingStream(inner)
    {
        private long _read;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ThrowIfPastHalf();
            var n = Inner.Read(buffer);
            _read += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfPastHalf();
            var n = await Inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _read += n;
            return n;
        }

        private void ThrowIfPastHalf()
        {
            if (_read > 0 && _read >= Inner.Length / 2)
            {
                throw new IOException("Test fault: read error (truncated file or failing card).");
            }
        }
    }
}

/// <summary>Base for wrapping streams.</summary>
public abstract class DelegatingStream(Stream inner) : Stream
{
    protected Stream Inner { get; } = inner;

    public override bool CanRead => Inner.CanRead;

    public override bool CanSeek => Inner.CanSeek;

    public override bool CanWrite => Inner.CanWrite;

    public override long Length => Inner.Length;

    public override long Position
    {
        get => Inner.Position;
        set => Inner.Position = value;
    }

    public override void Flush() => Inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);

    public override void SetLength(long value) => Inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await Inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
