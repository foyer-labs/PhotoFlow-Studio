// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using PhotoFlow.Core.IO;

namespace PhotoFlow.Sources.Ingest;

/// <summary>
/// Fast identity of a file (spec §9): xxHash3 of its length, first 64 KB and last 64 KB. Combined with the capture
/// time it recognizes photos already imported without reading whole files; the full hash comes from the copy.
/// </summary>
public static class QuickHash
{
    public const int SampleSize = 64 * 1024;

    public static async Task<string> ComputeAsync(IFileSystem fileSystem, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        var hasher = new XxHash3();
        await using var stream = fileSystem.OpenRead(path);
        var length = stream.Length;
        var lengthBytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
        hasher.Append(lengthBytes);

        var buffer = new byte[SampleSize];
        var head = await stream.ReadAtLeastAsync(buffer, (int)Math.Min(SampleSize, length), throwOnEndOfStream: false, ct).ConfigureAwait(false);
        hasher.Append(buffer.AsSpan(0, head));
        if (length > 2L * SampleSize)
        {
            stream.Seek(-SampleSize, SeekOrigin.End);
            var tail = await stream.ReadAtLeastAsync(buffer, SampleSize, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            hasher.Append(buffer.AsSpan(0, tail));
        }
        else if (length > head)
        {
            var rest = await stream.ReadAtLeastAsync(buffer, (int)(length - head), throwOnEndOfStream: false, ct).ConfigureAwait(false);
            hasher.Append(buffer.AsSpan(0, rest));
        }

        return hasher.GetCurrentHashAsUInt64().ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>Full content hash as stored in the catalog (hex of the xxHash3 computed during the copy).</summary>
    public static string Format(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>xxHash3 of the whole file, same value as the verified copy (background identity of archives).</summary>
    public static async Task<string> ComputeFullAsync(IFileSystem fileSystem, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        var hasher = new XxHash3();
        var buffer = new byte[1 << 20];
        await using var stream = fileSystem.OpenRead(path);
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hasher.Append(buffer.AsSpan(0, read));
        }

        return Format(hasher.GetCurrentHashAsUInt64());
    }
}
