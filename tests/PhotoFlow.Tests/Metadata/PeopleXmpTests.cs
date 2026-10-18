// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Data;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Metadata;

/// <summary>
/// People in XMP (spec §4, §7): named faces become MWG regions and the keywords Name / Persone|Name, read back by
/// exiftool; a region written by other software stays; unnamed groups are not written.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class PeopleXmpTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-people-xmp");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Named_people_become_regions_and_keywords_and_foreign_regions_stay()
    {
        await using var exifTool = ExifToolFixture.Start();
        var root = _temp.Sub("library");
        var path = SyntheticImages.WriteJpeg(Path.Combine(root, "family.jpg"), 400, 300);

        // Another program already wrote a region.
        await exifTool.ExecuteAsync(["-overwrite_original",
            "-XMP-mwg-rs:RegionInfo={AppliedToDimensions={W=400,H=300,Unit=pixel},RegionList=[{Area={X=0.8,Y=0.2,W=0.1,H=0.1,Unit=normalized},Name=Zia Lina,Type=Face}]}",
            path], Ct);

        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.EmbeddedForNonRaw, false, LibraryOrigin.Import, null), Ct);
        var assetId = await catalog.Assets.AddAsync(new Asset(0, "q", "f", "JPG", "jpeg", 400, 300, null, DateSource.Unknown, null, AssetState.Active), Ct);
        await catalog.Assets.AddLocationAsync(new AssetLocation(0, assetId, libraryId, "family.jpg", DateTimeOffset.UtcNow), Ct);
        await catalog.Analysis.ReplaceAutomaticFacesAsync(assetId,
        [
            new Face(0, assetId, new BoundingBox(0.1f, 0.2f, 0.3f, 0.5f), null, 0.9, EyeState.Open, EyeState.Open, null, null, null, null, "t"),
            new Face(0, assetId, new BoundingBox(0.5f, 0.2f, 0.6f, 0.4f), null, 0.9, EyeState.Open, EyeState.Open, null, null, null, null, "t"),
        ], Ct);
        var faces = await catalog.Analysis.GetFacesAsync(assetId, Ct);
        var sara = await catalog.People.CreatePersonAsync("Sara, la sposa", Ct);
        var unnamed = await catalog.People.CreatePersonAsync(null, Ct);
        await catalog.People.AssignManualAsync([faces[0].Id], sara, Ct);
        await catalog.People.AssignManualAsync([faces[1].Id], unnamed, Ct);
        await catalog.Decisions.SetUserAsync(assetId, CullingOutcome.Kept, 4, null, Ct);

        var summary = await new XmpPublisher(catalog, new ExifToolMetadataWriter(exifTool, new PhysicalFileSystem())).WriteAsync([assetId], prudentMode: true, null, Ct);

        Assert.Equal(1, summary.Written);
        var xmp = await PacketAsync(exifTool, path);
        Assert.Contains("<mwg-rs:Name>Sara, la sposa</mwg-rs:Name>", xmp, StringComparison.Ordinal);
        Assert.Contains("<mwg-rs:Name>Zia Lina</mwg-rs:Name>", xmp, StringComparison.Ordinal);
        Assert.DoesNotContain("Persona", xmp, StringComparison.Ordinal);
        Assert.Contains("Sara, la sposa", await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-dc:Subject"), StringComparison.Ordinal);
        Assert.Contains("Persone|Sara, la sposa", await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-lr:HierarchicalSubject"), StringComparison.Ordinal);
        Assert.Contains("<stArea:x>0.2</stArea:x>", xmp, StringComparison.Ordinal); // centre of 0.1..0.3

        // Writing again does not duplicate the regions.
        await catalog.XmpSync.MarkPendingAsync(assetId, null, Ct);
        await new XmpPublisher(catalog, new ExifToolMetadataWriter(exifTool, new PhysicalFileSystem())).WriteAsync([assetId], prudentMode: true, null, Ct);
        var again = await PacketAsync(exifTool, path);
        Assert.Equal(2, again.Split("<mwg-rs:Name>").Length - 1);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>The XMP packet as written (tag names of a long-running session can differ: the content cannot).</summary>
    private static async Task<string> PacketAsync(ExifToolSession exifTool, string path) =>
        (await exifTool.ExecuteAsync(["-xmp", "-b", path], Ct)).Output;
}
