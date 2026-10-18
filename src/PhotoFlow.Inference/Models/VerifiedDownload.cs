// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace PhotoFlow.Inference.Models;

public sealed record DownloadProgress(string Item, long DownloadedBytes, long TotalBytes);

/// <summary>
/// Download with SHA-256 computed while streaming; the file gets its final name only after verification.
/// A failed attempt leaves a ".part" file that the next attempt overwrites (nothing is deleted, I4).
/// </summary>
internal static class VerifiedDownload
{
    public static async Task FetchAsync(HttpClient http, Uri url, string target, string expectedSha256, long expectedSize, string label,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(target) && string.Equals(await Sha256Async(target, ct).ConfigureAwait(false), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".part";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                progress?.Report(new DownloadProgress(label, total, expectedSize));
            }
        }

        var actual = Convert.ToHexString(sha.GetHashAndReset());
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label}: SHA-256 {actual} does not match {expectedSha256}.");
        }

        if (File.Exists(target))
        {
            File.Replace(partial, target, destinationBackupFileName: null);
        }
        else
        {
            File.Move(partial, target);
        }
    }

    /// <summary>
    /// Copy from a folder chosen by the user (offline package): read only, SHA-256 and size checked like a download,
    /// final name given only after verification.
    /// </summary>
    public static async Task CopyAsync(string source, string target, string expectedSha256, long expectedSize, string label, CancellationToken ct)
    {
        if (File.Exists(target) && string.Equals(await Sha256Async(target, ct).ConfigureAwait(false), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (new FileInfo(source).Length != expectedSize)
        {
            throw new InvalidDataException($"{label}: size {new FileInfo(source).Length} does not match {expectedSize}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".part";
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }

        var actual = Convert.ToHexString(sha.GetHashAndReset());
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label}: SHA-256 {actual} does not match {expectedSha256}.");
        }

        if (File.Exists(target))
        {
            File.Replace(partial, target, destinationBackupFileName: null);
        }
        else
        {
            File.Move(partial, target);
        }
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }
}
