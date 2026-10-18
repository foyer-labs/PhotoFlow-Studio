// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Data;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Metadata;

/// <summary>
/// Acceptance of phase 5 "Modifiche XMP fatte in Bridge importate come manuali" (spec §4, written before the code):
/// after PhotoFlow writes the XMP, another program changes rating, label, keywords and description (exiftool, as Bridge
/// would). The sync imports the changes as the user's: a keyword added becomes a manual tag, an automatic tag removed is
/// suppressed (the external version wins), a manual tag is never removed, the description becomes the user's caption.
/// On rating and label the most recent change wins, with a log. A file nobody changed is not read again.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class ExternalXmpSyncTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-xmp-sync");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Changes_made_by_another_program_are_imported_as_manual()
    {
        await using var exifTool = ExifToolFixture.Start();
        var root = _temp.Sub("library");
        var path = SyntheticImages.WriteJpeg(Path.Combine(root, "lake.jpg"), 300, 200);
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.EmbeddedForNonRaw, false, LibraryOrigin.Import, null), Ct);
        var assetId = await catalog.Assets.AddAsync(new Asset(0, "q", "f", "JPG", "jpeg", 300, 200, null, DateSource.Unknown, null, AssetState.Active), Ct);
        await catalog.Assets.AddLocationAsync(new AssetLocation(0, assetId, libraryId, "lake.jpg", DateTimeOffset.UtcNow), Ct);
        await catalog.Tags.SeedVocabularyAsync([new("sunset", "tramonto", "Natura"), new("lake", "lago", "Natura")], TagSource.RamPlus, Ct);
        var sunset = (await catalog.Tags.FindAsync("sunset", Ct))!;
        await catalog.Tags.UpdateAsync(sunset.Id, "tramonto", reviewed: true, "Natura", blacklisted: false, Ct);
        await catalog.Tags.ReplaceAutomaticAsync(assetId, TagSource.RamPlus, "ram@1", [("sunset", 0.9), ("lake", 0.8)], Ct);
        await catalog.Tags.AddManualAsync(assetId, "vacanze", Ct);
        await catalog.Decisions.SetUserAsync(assetId, CullingOutcome.Kept, 3, null, Ct);

        var fileSystem = new PhysicalFileSystem();
        var reader = new ExifToolMetadataReader(exifTool, fileSystem);
        var publisher = new XmpPublisher(catalog, new ExifToolMetadataWriter(exifTool, fileSystem), reader: reader);
        var sync = new ExternalXmpSync(catalog, reader);
        Assert.Equal(1, (await publisher.WriteAsync([assetId], prudentMode: true, null, Ct)).Written);

        // Nothing changed outside: nothing to import, the file is not read again.
        var quiet = await sync.SyncAsync([assetId], null, Ct);
        Assert.Equal((0, 0), (quiet.Changed, quiet.Read));

        // "Bridge": 5 stars, green label, "Lago di Garda" added, sunset/tramonto and vacanze removed, a description.
        await exifTool.ExecuteAsync(["-overwrite_original", "-XMP-xmp:Rating=5", "-XMP-xmp:Label=Green",
            "-XMP-dc:Subject-=sunset", "-XMP-dc:Subject-=tramonto", "-XMP-dc:Subject-=vacanze", "-XMP-dc:Subject+=Lago di Garda",
            "-XMP-dc:Description=Tramonto sul lago", path], Ct);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var report = await sync.SyncAsync([assetId], null, Ct);
        Assert.Equal(1, report.Changed);
        var decision = (await catalog.Decisions.GetAsync(assetId, Ct))!;
        Assert.Equal((5, "Green"), (decision.Rating, decision.Label));
        var tags = await catalog.Tags.GetForAssetAsync(assetId, Ct);
        Assert.Equal(AssetTagState.Suppressed, tags.Single(t => t.Tag.Key == "sunset").Assignment.State);            // automatic: the external version wins
        Assert.Equal(AssetTagState.Active, tags.Single(t => t.Tag.Key == "lake").Assignment.State);                  // untouched
        var garda = tags.Single(t => t.Tag.English == "Lago di Garda" || t.Tag.Key == "Lago di Garda");
        Assert.Equal((AssetTagState.Active, TagSource.Manual), (garda.Assignment.State, garda.Assignment.Source));
        Assert.Equal(AssetTagState.Active, tags.Single(t => t.Tag.Key == "vacanze").Assignment.State);              // manual: never removed
        Assert.Contains(report.Log, l => l.Field == "keyword" && l.Value == "vacanze" && l.Winner == XmpSyncWinner.Catalog);
        Assert.Equal(("Tramonto sul lago", CaptionOrigin.Manual), (await catalog.Captions.GetAsync(assetId, Ct)) is { } c ? (c.Text, c.Origin) : default);
        Assert.True((await catalog.XmpSync.GetAsync(assetId, Ct))!.WritePending); // "vacanze" goes back in the file

        // The next write puts the manual tag back and keeps what came from outside.
        await publisher.WriteAsync([assetId], prudentMode: true, null, Ct);
        var subject = await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-dc:Subject");
        Assert.Contains("vacanze", subject, StringComparison.Ordinal);
        Assert.Contains("Lago di Garda", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("sunset", subject, StringComparison.Ordinal);
        Assert.Equal("5", (await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-xmp:Rating")).Trim());

        // Conflict on the rating: the user changes it in PhotoFlow after an older external change → PhotoFlow wins, logged.
        await exifTool.ExecuteAsync(["-overwrite_original", "-XMP-xmp:Rating=1", path], Ct);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        await catalog.Decisions.SetUserAsync(assetId, CullingOutcome.Kept, 2, "Green", Ct);
        var conflict = await sync.SyncAsync([assetId], null, Ct);
        Assert.Equal(2, (await catalog.Decisions.GetAsync(assetId, Ct))!.Rating);
        Assert.Contains(conflict.Log, l => l.Field == "rating" && l.Winner == XmpSyncWinner.Catalog);
        Assert.NotEmpty(await catalog.XmpSync.ListLogAsync(assetId, Ct));
    }

    [Fact]
    public async Task Face_regions_named_by_another_program_can_be_imported_as_people()
    {
        await using var exifTool = ExifToolFixture.Start();
        var root = _temp.Sub("library-regions");
        var path = SyntheticImages.WriteJpeg(Path.Combine(root, "family.jpg"), 400, 300);
        await exifTool.ExecuteAsync(["-overwrite_original", "-use", "MWG",
            "-XMP-mwg-rs:RegionInfo={AppliedToDimensions={W=400,H=300,Unit=pixel},RegionList=[{Area={X=0.25,Y=0.3,W=0.1,H=0.15,Unit=normalized},Name=Anna Rossi,Type=Face},{Area={X=0.7,Y=0.3,W=0.1,H=0.15,Unit=normalized},Name=Marco Bianchi,Type=Face}]}",
            path], Ct);
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "r.pfdb"), Path.Combine(_temp.Path, "rb")), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.EmbeddedForNonRaw, false, LibraryOrigin.ExistingArchive, null), Ct);
        var assetId = await catalog.Assets.AddAsync(new Asset(0, "q", "f", "JPG", "jpeg", 400, 300, null, DateSource.Unknown, null, AssetState.Active), Ct);
        await catalog.Assets.AddLocationAsync(new AssetLocation(0, assetId, libraryId, "family.jpg", DateTimeOffset.UtcNow), Ct);
        Face Detected(float cx) => new(0, assetId, new PhotoFlow.Core.Analysis.BoundingBox(cx - 0.05f, 0.23f, cx + 0.05f, 0.38f), null, 0.9, EyeState.Open, EyeState.Open, null, null, null, null, "det@1");
        await catalog.Analysis.ReplaceAutomaticFacesAsync(assetId, [Detected(0.26f), Detected(0.69f)], Ct);
        var marco = await catalog.People.CreatePersonAsync("Marco Bianchi", Ct);

        // First look at a photo PhotoFlow never wrote: what is in its XMP is imported; then the named regions.
        var sync = new ExternalXmpSync(catalog, new ExifToolMetadataReader(exifTool, new PhysicalFileSystem()));
        var report = await sync.SyncAsync([assetId], null, Ct);
        Assert.Equal(["Anna Rossi"], report.Log.Where(l => l.Field == "region").Select(l => l.Value)); // Marco is already a person
        Assert.Equal((1, 2), await sync.ImportNamedRegionsAsync(Ct));
        var faces = await catalog.Analysis.GetFacesAsync(assetId, Ct);
        var anna = (await catalog.People.ListPersonsAsync(Ct)).Single(p => p.Person.Name == "Anna Rossi").Person.Id;
        Assert.Equal([anna, marco], faces.OrderBy(f => f.Box.X1).Select(f => f.PersonId!.Value));
        Assert.All(faces, f => Assert.Equal(AssignmentOrigin.Manual, f.AssignmentOrigin));
        Assert.Equal((0, 0), await sync.ImportNamedRegionsAsync(Ct)); // nothing left to do
    }

    public void Dispose() => _temp.Dispose();
}
