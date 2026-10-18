// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using ImageMagick;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Data;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tests.Support;
using SkiaSharp;

namespace PhotoFlow.Tests.Scenarios;

/// <summary>
/// Acceptance scenario of phase 1b (spec §13): in-place analysis of 10,000 mixed files (JPEG, RAW+JPEG twins, PNG,
/// TIFF, WebP, HEIC), killed midway and resumed without redoing work; files with a wrong extension reach the right
/// handler; damaged files end in the report and stay intact. The analysis is the real one (format handlers and
/// culling signals), without models.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Scenario)]
public sealed class ArchiveScenarioTests : IDisposable
{
    public const int TotalFiles = 10_000;
    private readonly TempFolder _temp = new("pf-archive-scenario");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CatalogPath => Path.Combine(_temp.Path, "catalog.pfdb");

    private string Backups => Path.Combine(_temp.Path, "backups");

    [Fact]
    public async Task Ten_thousand_mixed_files_killed_and_resumed_without_redoing_work()
    {
        var watch = Stopwatch.StartNew();
        var archive = _temp.Sub("archive");
        var expected = WriteArchive(archive);
        Assert.Equal(TotalFiles, Directory.EnumerateFiles(archive, "*", SearchOption.AllDirectories).Count());
        var before = FolderSnapshot.Take(archive);
        var generated = watch.Elapsed;

        long jobId;
        long libraryId;
        await using (var catalog = await OpenAsync())
        {
            libraryId = await catalog.Libraries.AddAsync(new Library(0, "Archivio", archive, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);
            jobId = await ArchiveJobRunner.CreateAsync(catalog, new PhysicalFileSystem(),
                new ArchiveJobSettings(libraryId, "archive", ArchiveOrder.ByFolder, null, 4), Ct);
            Assert.Equal(TotalFiles, (await catalog.Jobs.GetItemsAsync(jobId, Ct)).Count);
        }

        // First run: killed (whole process) after a quarter of the files.
        watch.Restart();
        var firstLog = Path.Combine(_temp.Path, "processed-1.log");
        using (var host = ScenarioHostProcess.Start(CatalogPath, Backups, jobId, firstLog, 0, "archive"))
        {
            await ScenarioHostProcess.WaitForAsync(host,
                line => line.StartsWith("copied", StringComparison.Ordinal) && ScenarioHostProcess.Completed(line) >= TotalFiles / 4 ? "kill" : null,
                TimeSpan.FromMinutes(10));
            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync(Ct);
        }

        var firstRun = watch.Elapsed;
        HashSet<string> doneBeforeKill;
        await using (var catalog = await OpenAsync())
        {
            var items = await catalog.Jobs.GetItemsAsync(jobId, Ct);
            Assert.Equal(JobState.Running, (await catalog.Jobs.GetAsync(jobId, Ct))!.State); // left running by the crash
            doneBeforeKill = items.Where(i => i.State == JobItemState.Done).Select(i => i.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.InRange(doneBeforeKill.Count, TotalFiles / 4, TotalFiles - 1);
        }

        // Second run: the interrupted job is detected and resumed to the end.
        watch.Restart();
        var secondLog = Path.Combine(_temp.Path, "processed-2.log");
        using (var host = ScenarioHostProcess.Start(CatalogPath, Backups, jobId, secondLog, 0, "archive"))
        {
            var lines = new List<string>();
            await ScenarioHostProcess.WaitForAsync(host, line => { lines.Add(line); return null; }, TimeSpan.FromMinutes(20));
            await host.WaitForExitAsync(Ct);
            Assert.Equal(0, host.ExitCode);
            Assert.Contains("interrupted 1", lines);
            Assert.Contains("state Completed", lines);
        }

        var secondRun = watch.Elapsed;

        // Nothing finished before the kill was analyzed again.
        Assert.DoesNotContain(File.ReadAllLines(secondLog), doneBeforeKill.Contains);

        await using (var catalog = await OpenAsync())
        {
            var items = await catalog.Jobs.GetItemsAsync(jobId, Ct);
            var byPath = items.ToDictionary(i => Path.GetRelativePath(archive, i.SourcePath), StringComparer.OrdinalIgnoreCase);

            // Damaged files, and only them, are in the report.
            var failed = items.Where(i => i.State == JobItemState.Failed).Select(i => Path.GetRelativePath(archive, i.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(expected.Corrupt.Order(StringComparer.OrdinalIgnoreCase), failed.Order(StringComparer.OrdinalIgnoreCase));
            Assert.All(items.Where(i => i.State == JobItemState.Failed), i => Assert.False(string.IsNullOrWhiteSpace(i.Error)));
            Assert.Equal(TotalFiles - expected.Corrupt.Count, items.Count(i => i.State == JobItemState.Done));

            // The handler comes from the content, whatever the extension says.
            foreach (var (relative, kind) in expected.WrongExtension)
            {
                var asset = await catalog.Assets.GetAsync(byPath[relative].AssetId!.Value, Ct);
                Assert.Equal(kind, asset!.Handler);
            }

            // Every photo has a decision; twins share it; every asset gets its full hash in the final pass.
            foreach (var item in items.Where(i => i.State == JobItemState.Done))
            {
                Assert.NotNull(await catalog.Decisions.GetAsync(item.AssetId!.Value, Ct));
            }

            Assert.Empty(await catalog.Assets.ListWithoutFullHashAsync(libraryId, 10, Ct));
        }

        // The archive is only read: same files, same bytes, nothing added.
        Assert.Equal(before, FolderSnapshot.Take(archive));

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"10,000 files: generated in {generated.TotalSeconds:F0} s; first run {firstRun.TotalSeconds:F0} s until {doneBeforeKill.Count} done (killed); " +
            $"resume {secondRun.TotalSeconds:F0} s; {expected.Corrupt.Count} damaged in the report; {expected.WrongExtension.Count} wrong extensions handled.");
    }

    public void Dispose() => _temp.Dispose();

    private Task<SqliteCatalog> OpenAsync() => ScenarioHostProcess.OpenCatalogAsync(CatalogPath, Backups);

    private sealed record Expected(List<string> Corrupt, List<(string Relative, string Kind)> WrongExtension);

    /// <summary>
    /// 10,000 files over 12 folders: 1,500 RAW+JPEG twins (fake NEF, analyzed through the JPEG), PNG, TIFF, WebP,
    /// 20 HEIC (the public CC0 sample, so duplicates of one photo), 80 files with a wrong extension, 30 damaged files,
    /// the rest JPEG. Every image is a different small picture.
    /// </summary>
    private static Expected WriteArchive(string root)
    {
        var expected = new Expected([], []);
        var heic = File.ReadAllBytes(Repository.PathOf("tests", "data", "public", "synthetic-gradient.heic"));
        var written = 0;
        var seed = 0;

        string Next(string extension)
        {
            var folder = Path.Combine(root, $"{2015 + (written % 12)}", $"{(written / 12 % 6) + 1:00}");
            Directory.CreateDirectory(folder);
            written++;
            return Path.Combine(folder, $"P{written:00000}{extension}");
        }

        string Relative(string path) => Path.GetRelativePath(root, path);

        for (var i = 0; i < 1500; i++)
        {
            var jpeg = Next(".JPG");
            File.WriteAllBytes(jpeg, Encode(SKEncodedImageFormat.Jpeg, ++seed));
            File.WriteAllBytes(Path.ChangeExtension(jpeg, ".NEF"), FakeRaw(seed));
            written++;
        }

        for (var i = 0; i < 1200; i++)
        {
            File.WriteAllBytes(Next(".png"), Encode(SKEncodedImageFormat.Png, ++seed));
        }

        for (var i = 0; i < 800; i++)
        {
            File.WriteAllBytes(Next(".tif"), Tiff(++seed));
        }

        for (var i = 0; i < 400; i++)
        {
            File.WriteAllBytes(Next(".webp"), Encode(SKEncodedImageFormat.Webp, ++seed));
        }

        for (var i = 0; i < 20; i++)
        {
            File.WriteAllBytes(Next(".heic"), heic);
        }

        foreach (var (content, extension, kind, count) in new[] { ("png", ".JPG", "png", 30), ("jpeg", ".png", "jpeg", 30), ("tiff", ".jpg", "tiff", 20) })
        {
            for (var i = 0; i < count; i++)
            {
                var path = Next(extension);
                File.WriteAllBytes(path, content switch
                {
                    "png" => Encode(SKEncodedImageFormat.Png, ++seed),
                    "jpeg" => Encode(SKEncodedImageFormat.Jpeg, ++seed),
                    _ => Tiff(++seed),
                });
                expected.WrongExtension.Add((Relative(path), kind));
            }
        }

        // Damaged: JPEG marker then noise, PNG cut in half, a text file named .png.
        for (var i = 0; i < 30; i++)
        {
            var random = new Random(100_000 + i);
            var (extension, bytes) = (i % 3) switch
            {
                0 => (".JPG", (byte[])[0xFF, 0xD8, 0xFF, 0xE0, .. Noise(random, 4000)]),
                1 => (".png", Encode(SKEncodedImageFormat.Png, ++seed)[..200]),
                _ => (".png", "not an image, just text"u8.ToArray()),
            };
            var path = Next(extension);
            File.WriteAllBytes(path, bytes);
            expected.Corrupt.Add(Relative(path));
        }

        while (written < TotalFiles)
        {
            File.WriteAllBytes(Next(".JPG"), Encode(SKEncodedImageFormat.Jpeg, ++seed));
        }

        return expected;
    }

    private static byte[] Noise(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>A 48×32 picture unique to <paramref name="seed"/> (gradient plus a few seeded pixels).</summary>
    private static byte[] Encode(SKEncodedImageFormat format, int seed)
    {
        using var bitmap = new SKBitmap(48, 32, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var random = new Random(seed);
        var pixels = new byte[48 * 32 * 4];
        for (var p = 0; p < 48 * 32; p++)
        {
            pixels[p * 4] = (byte)(p % 48 * 5);
            pixels[(p * 4) + 1] = (byte)(p / 48 * 7);
            pixels[(p * 4) + 2] = (byte)random.Next(256);
            pixels[(p * 4) + 3] = 255;
        }

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] Tiff(int seed)
    {
        using var image = new MagickImage(Encode(SKEncodedImageFormat.Png, seed));
        return image.ToByteArray(MagickFormat.Tiff);
    }

    /// <summary>TIFF header and seeded noise: classified as RAW by content, analyzed through its JPEG twin.</summary>
    private static byte[] FakeRaw(int seed)
    {
        var bytes = Noise(new Random(seed), 16 * 1024);
        bytes[0] = 0x4D;
        bytes[1] = 0x4D;
        bytes[2] = 0x00;
        bytes[3] = 0x2A;
        return bytes;
    }
}
