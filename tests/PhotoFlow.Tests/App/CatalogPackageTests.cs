// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Data;
using PhotoFlow.Faces.Protection;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// Acceptance of phase 5 "Esportazione e importazione su altro percorso con rimappatura riuscita" (spec §9, written
/// before the code): a catalog with a library, tags, people with fingerprints, decisions, captions, a custom profile
/// and thumbnails goes into one .pfcatalog file; imported on another profile with the library moved elsewhere, the
/// content is the same, the paths point to the new place (XMP written there), the fingerprints are encrypted again
/// with the local key when the export password is given, and left out (to recompute) otherwise. The original is never
/// touched.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class CatalogPackageTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-package");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_catalog_moves_to_another_path_with_its_people_and_fingerprints()
    {
        // Source PC.
        var source = new DefaultAppPaths(_temp.Sub("pc-a"));
        var rootA = _temp.Sub("photos-a");
        Directory.CreateDirectory(Path.Combine(rootA, "2026"));
        SyntheticImages.WriteJpeg(Path.Combine(rootA, "2026", "lake.jpg"), 300, 200);
        var keyA = new FingerprintCipher(RandomNumberGenerator.GetBytes(32));
        var fingerprint = Enumerable.Range(0, 512).Select(i => MathF.Sin(i)).ToArray();
        long libraryId, assetId, personId;
        await using (var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(source.CatalogPath, source.BackupsDirectory), Ct))
        {
            libraryId = await catalog.Libraries.AddAsync(new Library(0, "Archivio", rootA, WritePolicy.EmbeddedForNonRaw, false, LibraryOrigin.ExistingArchive, null), Ct);
            assetId = await catalog.Assets.AddAsync(new Asset(0, "q1", "f1", "JPG", "jpeg", 300, 200, null, DateSource.Unknown, null, AssetState.Active), Ct);
            await catalog.Assets.AddLocationAsync(new AssetLocation(0, assetId, libraryId, Path.Combine("2026", "lake.jpg"), DateTimeOffset.UtcNow), Ct);
            await catalog.Tags.AddManualAsync(assetId, "lago di Como", Ct);
            await catalog.Decisions.SetUserAsync(assetId, CullingOutcome.Kept, 4, null, Ct);
            await catalog.Captions.SetManualAsync(assetId, "it", "Il lago al tramonto.", Ct);
            await catalog.Analysis.ReplaceAutomaticFacesAsync(assetId,
                [new Face(0, assetId, new BoundingBox(0.4f, 0.3f, 0.5f, 0.45f), null, 0.9, EyeState.Open, EyeState.Open, keyA.Protect(fingerprint), null, null, null, "det@1", null, "arc@1/arcface")], Ct);
            var face = (await catalog.Analysis.GetFacesAsync(assetId, Ct)).Single();
            personId = await catalog.People.CreatePersonAsync("Anna", Ct);
            await catalog.People.AssignManualAsync([face.Id], personId, Ct);
            await catalog.People.ReplacePrototypesAsync(personId, [new PersonPrototype(0, personId, face.Id, 2026, keyA.Protect(fingerprint), "arc@1/arcface")], Ct);

            Directory.CreateDirectory(source.ProfilesDirectory);
            await File.WriteAllTextAsync(Path.Combine(source.ProfilesDirectory, "custom-pista.v1.json"), "{\"id\":\"custom-pista\"}", Ct);
            var thumbnail = Path.Combine(source.ThumbnailsDirectory, "f1"[..2], "f1.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
            await File.WriteAllBytesAsync(thumbnail, [1, 2, 3], Ct);

            // Export with a password and the thumbnails.
            var package = Path.Combine(_temp.Path, "archivio.pfcatalog");
            var summary = await CatalogPackage.ExportAsync(catalog, source, keyA, package, new CatalogExportOptions(IncludeThumbnails: true, FingerprintPassword: "segreta"), Ct);
            Assert.Equal((1, 1, 1), (summary.Libraries, summary.Fingerprints, summary.Thumbnails));
        }

        var file = Path.Combine(_temp.Path, "archivio.pfcatalog");
        var manifest = CatalogPackage.ReadManifest(file);
        Assert.Equal([(libraryId, rootA)], manifest.Libraries.Select(l => (l.Id, l.RootPath)));
        Assert.True(manifest.HasFingerprints);

        // Destination PC: the photos are elsewhere.
        var destination = new DefaultAppPaths(_temp.Sub("pc-b"));
        var rootB = _temp.Sub("photos-b");
        Directory.CreateDirectory(Path.Combine(rootB, "2026"));
        SyntheticImages.WriteJpeg(Path.Combine(rootB, "2026", "lake.jpg"), 300, 200);
        var keyB = new FingerprintCipher(RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<CatalogPackageException>(() =>
            CatalogPackage.PrepareImportAsync(file, destination, new Dictionary<long, string> { [libraryId] = rootB }, "sbagliata", keyB, Ct));
        var prepared = await CatalogPackage.PrepareImportAsync(file, destination, new Dictionary<long, string> { [libraryId] = rootB }, "segreta", keyB, Ct);
        Assert.StartsWith(destination.BackupsDirectory, prepared, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(destination.ProfilesDirectory, "custom-pista.v1.json")));
        Assert.True(File.Exists(Path.Combine(destination.ThumbnailsDirectory, "f1", "f1.jpg")));

        await using (var imported = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(prepared, destination.BackupsDirectory), Ct))
        {
            Assert.Equal(rootB, (await imported.Libraries.GetAsync(libraryId, Ct))!.RootPath);
            Assert.Contains((await imported.Tags.GetForAssetAsync(assetId, Ct)), t => t.Tag.Key == "lago di Como");
            Assert.Equal(4, (await imported.Decisions.GetAsync(assetId, Ct))!.Rating);
            Assert.Equal("Il lago al tramonto.", (await imported.Captions.GetAsync(assetId, Ct))!.Text);
            var face = (await imported.Analysis.GetFacesAsync(assetId, Ct)).Single();
            Assert.Equal((personId, AssignmentOrigin.Manual), (face.PersonId!.Value, face.AssignmentOrigin!.Value));
            Assert.Equal(fingerprint, keyB.Unprotect(face.EncryptedEmbedding));
            Assert.Equal(fingerprint, keyB.Unprotect((await imported.People.ListPrototypesAsync(Ct)).Single().EncryptedEmbedding));
            Assert.Throws<FingerprintKeyUnavailableException>(() => keyA.Unprotect(face.EncryptedEmbedding));

            // The XMP goes to the new place.
            await using var exifTool = ExifToolFixture.Start();
            var written = await new XmpPublisher(imported, new ExifToolMetadataWriter(exifTool, new PhysicalFileSystem())).WriteAsync([assetId], prudentMode: true, null, Ct);
            Assert.Equal(1, written.Written);
            Assert.Equal("4", (await ExifToolFixture.ReadTagAsync(exifTool, Path.Combine(rootB, "2026", "lake.jpg"), "XMP-xmp:Rating")).Trim());
            Assert.Equal(string.Empty, (await ExifToolFixture.ReadTagAsync(exifTool, Path.Combine(rootA, "2026", "lake.jpg"), "XMP-xmp:Rating")).Trim());
        }

        // Without a password the fingerprints stay behind; people and assignments travel.
        await using (var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(source.CatalogPath, source.BackupsDirectory), Ct))
        {
            var bare = Path.Combine(_temp.Path, "senza-impronte.pfcatalog");
            Assert.Equal(0, (await CatalogPackage.ExportAsync(catalog, source, keyA, bare, new CatalogExportOptions(IncludeThumbnails: false, FingerprintPassword: null), Ct)).Fingerprints);
            Assert.False(CatalogPackage.ReadManifest(bare).HasFingerprints);
            var other = new DefaultAppPaths(_temp.Sub("pc-c"));
            var copy = await CatalogPackage.PrepareImportAsync(bare, other, new Dictionary<long, string>(), null, null, Ct);
            await using var imported = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(copy, other.BackupsDirectory), Ct);
            var face = (await imported.Analysis.GetFacesAsync(assetId, Ct)).Single();
            Assert.Equal(personId, face.PersonId);
            Assert.Null(face.EncryptedEmbedding);
            Assert.Empty(await imported.People.ListPrototypesAsync(Ct));
            Assert.Equal(rootA, (await imported.Libraries.GetAsync(libraryId, Ct))!.RootPath); // not remapped: same path
        }

        // The source catalog still has its fingerprints.
        await using (var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(source.CatalogPath, source.BackupsDirectory), Ct))
        {
            Assert.Equal(fingerprint, keyA.Unprotect((await catalog.Analysis.GetFacesAsync(assetId, Ct)).Single().EncryptedEmbedding));
        }
    }

    public void Dispose() => _temp.Dispose();
}
