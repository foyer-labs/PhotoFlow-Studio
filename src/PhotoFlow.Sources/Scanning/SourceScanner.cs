// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Sources;

namespace PhotoFlow.Sources.Scanning;

/// <summary>
/// Read-only scan of an import source (card or folder): every file is classified from its first bytes
/// (signature), the extension only disambiguates TIFF-based RAW formats.
/// </summary>
public sealed class SourceScanner : ISource
{
    private readonly IFileSystem _fileSystem;
    private readonly string _root;

    public SourceScanner(IFileSystem fileSystem, string root)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _fileSystem = fileSystem;
        _root = Path.GetFullPath(root);
    }

    public async IAsyncEnumerable<SourceItem> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var file in ScanAsync(ct).ConfigureAwait(false))
        {
            yield return file.Item;
        }
    }

    public async IAsyncEnumerable<ScannedFile> ScanAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var path in _fileSystem.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
            {
                continue; // system and hidden files (e.g. ._DSC_0001.NEF from macOS) are not photos
            }

            var metadata = _fileSystem.GetFileMetadata(path);
            var header = await ReadHeaderAsync(path, ct).ConfigureAwait(false);
            var item = new SourceItem(path, Path.GetRelativePath(_root, path), metadata.Length, metadata.LastWriteTimeUtc);
            yield return new ScannedFile(item, MediaClassifier.Classify(header, Path.GetExtension(path)));
        }
    }

    private async Task<byte[]> ReadHeaderAsync(string path, CancellationToken ct)
    {
        var buffer = new byte[16];
        await using var stream = _fileSystem.OpenRead(path);
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        return buffer[..read];
    }
}
