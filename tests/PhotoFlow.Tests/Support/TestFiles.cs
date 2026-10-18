// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoFlow.Tests.Support;

/// <summary>A temporary folder removed at the end of the test (test infrastructure only).</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder(string prefix = "pf")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts)
    {
        var path = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            foreach (var entry in new DirectoryInfo(Path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                entry.Attributes = FileAttributes.Normal;
            }

            new DirectoryInfo(Path).Attributes = FileAttributes.Directory;
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Leftovers in the temp folder are harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Synthetic files with real signatures (no personal photos, no license issues).</summary>
public static class SyntheticFiles
{
    private static readonly byte[] NefHeader = [0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE1];

    /// <summary>Creates a simulated memory card: <c>DCIM/100NIKON</c> with NEF and JPEG files of random content.</summary>
    public static IReadOnlyList<string> CreateCard(string root, int nefCount, int jpegCount, int seed = 42, int size = 64 * 1024)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "DCIM", "100NIKON")).FullName;
        var random = new Random(seed);
        var files = new List<string>();
        for (var i = 1; i <= nefCount; i++)
        {
            files.Add(Write(Path.Combine(folder, $"DSC_{i:0000}.NEF"), NefHeader, size, random));
        }

        for (var i = 1; i <= jpegCount; i++)
        {
            files.Add(Write(Path.Combine(folder, $"DSC_{nefCount + i:0000}.JPG"), JpegHeader, size / 2, random));
        }

        return files;
    }

    /// <summary>Marks every file and folder under <paramref name="root"/> as read-only, like a locked card.</summary>
    public static void MakeReadOnly(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        }
    }

    private static string Write(string path, byte[] header, int size, Random random)
    {
        var bytes = new byte[size];
        random.NextBytes(bytes);
        header.CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

/// <summary>Independent snapshot of a folder (SHA-256), used to verify invariants without the product's hashing.</summary>
public static class FolderSnapshot
{
    public static IReadOnlyDictionary<string, string> Take(string root, Func<string, bool>? include = null) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => include?.Invoke(f) ?? true)
            .ToDictionary(f => Path.GetRelativePath(root, f), Sha256, StringComparer.OrdinalIgnoreCase);

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>
/// Public RAW samples (CC0, raw.pixls.us) listed in tests/data/public/samples.json: downloaded once,
/// verified with SHA-256, cached outside the repository. Never committed.
/// </summary>
public static class PublicSamples
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static string CacheDirectory =>
        Environment.GetEnvironmentVariable("PHOTOFLOW_SAMPLES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoFlow.TestSamples");

    /// <summary>Path of a verified sample; downloads it if needed. Skips locally when offline, fails in CI.</summary>
    public static async Task<string> RequireAsync(string id)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Repository.PathOf("tests", "data", "public", "samples.json")));
        var sample = manifest.RootElement.GetProperty("samples").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);
        var expected = sample.GetProperty("sha256").GetString()!;
        var target = Path.Combine(CacheDirectory, sample.GetProperty("file").GetString()!);
        if (File.Exists(target) && FolderSnapshot.Sha256(target).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        Directory.CreateDirectory(CacheDirectory);
        try
        {
            var bytes = await Http.GetByteArrayAsync(new Uri(sample.GetProperty("url").GetString()!));
            var actual = Convert.ToHexString(SHA256.HashData(bytes));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Sample {id}: SHA-256 {actual} does not match {expected}.");
            }

            await File.WriteAllBytesAsync(target, bytes);
            return target;
        }
        catch (HttpRequestException e) when (Environment.GetEnvironmentVariable("CI") is null)
        {
            Assert.Skip($"Public sample {id} not available offline: {e.Message}");
            throw;
        }
    }
}
