// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Core.IO;

/// <summary>
/// The only file access used by sources, ingest and metadata. It deliberately has no delete operation:
/// nothing in PhotoFlow can delete a file (invariant I4). Tests wrap it to prove that the import
/// source is never written (invariant I1).
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption option);

    FileMetadata GetFileMetadata(string path);

    /// <summary>Opens a file for asynchronous sequential reading; other readers are allowed.</summary>
    Stream OpenRead(string path);

    /// <summary>Creates or truncates a file for writing (used only for PhotoFlow's own working files).</summary>
    Stream OpenWrite(string path);

    void CreateDirectory(string path);

    /// <summary>Renames a file; never overwrites an existing destination (throws <see cref="IOException"/>).</summary>
    void Move(string sourcePath, string destinationPath);

    /// <summary>
    /// Atomically replaces <paramref name="destinationPath"/> with <paramref name="replacementPath"/>, optionally keeping the
    /// previous content as <paramref name="backupPath"/>. Used only to embed metadata in a library file after verification.
    /// </summary>
    void Replace(string replacementPath, string destinationPath, string? backupPath);

    long GetAvailableFreeSpace(string path);
}

public sealed record FileMetadata(long Length, DateTimeOffset LastWriteTimeUtc, bool IsReadOnly);

/// <summary>Direct access to the local file system.</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    private const int BufferSize = 1 << 20;

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption option) =>
        Directory.EnumerateFiles(directory, searchPattern, option);

    public FileMetadata GetFileMetadata(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("File not found.", path);
        }

        return new FileMetadata(info.Length, info.LastWriteTimeUtc, info.IsReadOnly);
    }

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public Stream OpenWrite(string path) =>
        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void Move(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath, overwrite: false);

    public void Replace(string replacementPath, string destinationPath, string? backupPath) =>
        File.Replace(replacementPath, destinationPath, backupPath, ignoreMetadataErrors: true);

    public long GetAvailableFreeSpace(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new ArgumentException("Path has no root.", nameof(path));
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
