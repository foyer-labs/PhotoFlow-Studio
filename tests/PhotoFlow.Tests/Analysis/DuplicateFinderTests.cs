// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Duplicates;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Data;
using PhotoFlow.Imaging;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>
/// Duplicates (P5, F3-5, F3-6): exact copies in two folders of a library; a near duplicate (same picture, smaller) found
/// by CLIP and confirmed by the perceptual hash; a similar but different picture and a RAW+JPEG pair not proposed; the
/// copies across libraries (backups) not proposed; "not duplicates" remembered; the copy not kept moved into
/// <c>_duplicati</c> with nothing deleted.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class DuplicateFinderTests : IDisposable
{
    private const string Clip = "clip@test";
    private readonly TempFolder _temp = new("pf-duplicates");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Exact_and_near_duplicates_are_found_kept_apart_and_moved_on_request()
    {
        var fs = new PhysicalFileSystem();
        var operations = new SkiaImageOperations();
        var thumbnails = new ThumbnailCache(fs, _temp.Sub("thumbs"));
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var root = _temp.Sub("library");
        var backupRoot = _temp.Sub("backup");
        var library = await catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);
        var backup = await catalog.Libraries.AddAsync(new Library(0, "Backup", backupRoot, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);

        async Task<long> Photo(string relative, int seed, int width, float[] embedding, string handler = "jpeg", long libraryId = 0)
        {
            var file = SyntheticImages.WriteScene(Path.Combine(libraryId == backup ? backupRoot : root, relative), width, width * 2 / 3, seed);
            var id = await catalog.Assets.AddAsync(new Asset(0, $"q-{relative}", $"h-{relative}", "JPG", handler, width, width * 2 / 3, null, DateSource.Unknown, null, AssetState.Active), Ct);
            await catalog.Assets.AddLocationAsync(new AssetLocation(0, id, libraryId == 0 ? library : libraryId, relative, DateTimeOffset.UtcNow), Ct);
            await catalog.Analysis.SaveEmbeddingAsync(id, Clip, Vectors.Normalize(embedding), Ct);
            await using var stream = File.OpenRead(file);
            await thumbnails.StoreAsync($"h-{relative}", await operations.DecodeAsync(stream, Ct), Ct);
            return id;
        }

        Directory.CreateDirectory(Path.Combine(root, "2023"));
        Directory.CreateDirectory(Path.Combine(root, "export"));
        Directory.CreateDirectory(Path.Combine(root, "copia"));
        var original = await Photo("2023/ferragosto.jpg", 1, 600, [1, 0, 0, 0]);
        var export = await Photo("export/ferragosto_web.jpg", 1, 300, [0.98f, 0.1f, 0, 0]);          // same picture, smaller
        await Photo("2023/altro.jpg", 7, 600, [0.97f, 0, 0.2f, 0]);                                     // close for CLIP, different picture
        var jpeg = await Photo("2023/pair.jpg", 3, 600, [0, 1, 0, 0]);
        var raw = await Photo("2023/pair.nef", 3, 600, [0, 0.99f, 0.05f, 0], handler: "raw");
        await catalog.Assets.AddPairAsync(raw, jpeg, Ct);

        // Exact: the same content in two folders of the library, and once more in the backup library.
        await catalog.Assets.AddLocationAsync(new AssetLocation(0, original, library, "copia/ferragosto.jpg", DateTimeOffset.UtcNow), Ct);
        File.Copy(Path.Combine(root, "2023", "ferragosto.jpg"), Path.Combine(root, "copia", "ferragosto.jpg"));
        await catalog.Assets.AddLocationAsync(new AssetLocation(0, original, backup, "2023/ferragosto.jpg", DateTimeOffset.UtcNow), Ct);

        var finder = new DuplicateFinder(catalog, thumbnails, operations);
        var scan = await finder.ScanAsync(Clip, null, Ct);

        var exact = Assert.Single(scan.Exact);
        Assert.Equal(["2023/ferragosto.jpg", "copia/ferragosto.jpg"], exact.Members.Select(m => m.Location.RelativePath.Replace('\\', '/')).Order(StringComparer.Ordinal));
        Assert.All(exact.Members, m => Assert.Equal(library, m.Location.LibraryId));
        var near = Assert.Single(scan.Near);
        Assert.Equal([original, export], near.Members.Select(m => m.AssetId).Order());
        Assert.Equal(original, near.Members[near.Recommended].AssetId);                                // the larger one
        Assert.Equal(5, scan.Checked);

        // "Not duplicates" is remembered.
        await catalog.Duplicates.ExcludeAsync(near.Key, Ct);
        Assert.Empty((await finder.ScanAsync(Clip, null, Ct)).Near);

        // Keep the copy in 2023, move the other into _duplicati: renamed, never deleted; the catalog follows.
        var other = exact.Members.Single(m => m.Location.RelativePath.StartsWith("copia", StringComparison.Ordinal));
        var (moved, _) = await new LibraryMover(catalog, fs, "_duplicati").MoveAsync([other.Location], Ct);
        Assert.Equal(1, moved);
        Assert.True(File.Exists(Path.Combine(root, "_duplicati", "copia", "ferragosto.jpg")));
        Assert.True(File.Exists(Path.Combine(root, "2023", "ferragosto.jpg")));
        Assert.False(File.Exists(Path.Combine(root, "copia", "ferragosto.jpg")));
        Assert.Contains(await catalog.Assets.GetLocationsAsync(original, Ct), l => l.RelativePath.Replace('\\', '/') == "_duplicati/copia/ferragosto.jpg");
    }

    [Fact]
    [Trait(TestCategory.Name, TestCategory.Unit)]
    public void The_difference_hash_is_close_for_a_resized_copy_and_far_for_another_picture()
    {
        var operations = new SkiaImageOperations();
        var a = Decode(operations, SyntheticImages.WriteScene(Path.Combine(_temp.Path, "a.jpg"), 800, 600, 1));
        var b = Decode(operations, SyntheticImages.WriteScene(Path.Combine(_temp.Path, "b.jpg"), 200, 150, 1));
        var c = Decode(operations, SyntheticImages.WriteScene(Path.Combine(_temp.Path, "c.jpg"), 800, 600, 9));
        var (ha, hb, hc) = (DuplicateFinder.DifferenceHash(a, operations), DuplicateFinder.DifferenceHash(b, operations), DuplicateFinder.DifferenceHash(c, operations));
        Assert.InRange(System.Numerics.BitOperations.PopCount(ha ^ hb), 0, DuplicateFinder.MaxHashDistance);
        Assert.True(System.Numerics.BitOperations.PopCount(ha ^ hc) > DuplicateFinder.MaxHashDistance);
    }

    public void Dispose() => _temp.Dispose();

    private static PhotoFlow.Core.Imaging.RgbImage Decode(SkiaImageOperations operations, string path)
    {
        using var stream = File.OpenRead(path);
        return operations.DecodeAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
    }
}
