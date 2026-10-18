// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Data;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Tagging.Ocr;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Metadata;

/// <summary>
/// D18 and spec §8 (acceptance of phase 4 "Equipaggi: solo numero nell'XMP"): a car with one driver gets
/// Motorsport|Numero N, Motorsport|Pilota|Name and Motorsport|Team|Name; a crew only the number and the team, never the
/// names; numbers to review or out of the list are not written; the user's number is never replaced (I5).
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class MotorsportXmpTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-motorsport-xmp");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Crews_get_only_the_number_and_the_team_in_the_xmp()
    {
        await using var exifTool = ExifToolFixture.Start();
        var root = _temp.Sub("library");
        var path = SyntheticImages.WriteJpeg(Path.Combine(root, "monza.jpg"), 300, 200);
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.EmbeddedForNonRaw, false, LibraryOrigin.Import, null), Ct);
        var assetId = await catalog.Assets.AddAsync(new Asset(0, "q", "f", "JPG", "jpeg", 300, 200, null, DateSource.Unknown, null, AssetState.Active), Ct);
        await catalog.Assets.AddLocationAsync(new AssetLocation(0, assetId, libraryId, "monza.jpg", DateTimeOffset.UtcNow), Ct);
        await catalog.Decisions.SetUserAsync(assetId, CullingOutcome.Kept, 3, null, Ct);

        var list = await catalog.Motorsport.CreateListAsync("Coppa Italia Turismo 2026", "iscritti.csv", Ct);
        await catalog.Motorsport.ReplaceEntriesAsync(list,
        [
            new Entry(0, list, "27", ["Marco Ferri"], "Lario Corse", "Gr. A", "Civic"),
            new Entry(0, list, "112", ["Anna Riva", "Bruno Sala"], "Gruppo Quattro", "Gr. N", "Clio"),
            new Entry(0, list, "6", ["Andrea Neri"], "Neri Motorsport", null, null),
            new Entry(0, list, "9", ["Paolo Galli"], "Galli Racing", null, null),
        ], Ct);
        var entries = await catalog.Motorsport.GetEntriesAsync(list, Ct);
        var box = new BoundingBox(0.1f, 0.1f, 0.4f, 0.5f);
        await catalog.Motorsport.ReplaceAutomaticNumbersAsync(assetId,
        [
            new VehicleNumber(assetId, 0, box, "27", NumberState.Certain, NumberOrigin.Automatic, []),
            new VehicleNumber(assetId, 1, box, "112", NumberState.Certain, NumberOrigin.Automatic, []),
            new VehicleNumber(assetId, 2, box, null, NumberState.ToReview, NumberOrigin.Automatic, [new NumberCandidate("6", 0.48), new NumberCandidate("9", 0.44)]),
            new VehicleNumber(assetId, 3, box, null, NumberState.OutOfList, NumberOrigin.Automatic, [new NumberCandidate("348", 0.97)]),
        ], Ct);
        await MotorsportTags.ApplyAsync(catalog, assetId, entries, Ct);

        var publisher = new XmpPublisher(catalog, new ExifToolMetadataWriter(exifTool, new PhysicalFileSystem()));
        await publisher.WriteAsync([assetId], prudentMode: true, null, Ct);
        var hierarchy = await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-lr:HierarchicalSubject");
        var subject = await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-dc:Subject");
        foreach (var expected in new[] { "Motorsport|Numero 27", "Motorsport|Pilota|Marco Ferri", "Motorsport|Team|Lario Corse", "Motorsport|Numero 112", "Motorsport|Team|Gruppo Quattro" })
        {
            Assert.Contains(expected, hierarchy, StringComparison.Ordinal);
        }

        foreach (var absent in new[] { "Anna Riva", "Bruno Sala", "Numero 6", "Numero 9", "348", "Andrea Neri", "Paolo Galli" })
        {
            Assert.DoesNotContain(absent, hierarchy, StringComparison.Ordinal);
            Assert.DoesNotContain(absent, subject, StringComparison.Ordinal);
        }

        // The user decides the doubtful car: 9. A new analysis that reads 6 does not change it (I5).
        await catalog.Motorsport.SetManualNumberAsync(assetId, 2, "9", Ct);
        await catalog.Motorsport.ReplaceAutomaticNumbersAsync(assetId,
            [new VehicleNumber(assetId, 2, box, "6", NumberState.Certain, NumberOrigin.Automatic, [])], Ct);
        var numbers = await catalog.Motorsport.GetNumbersAsync(assetId, Ct);
        Assert.Equal(("9", NumberOrigin.Manual), (numbers.Single(n => n.Vehicle == 2).Number, numbers.Single(n => n.Vehicle == 2).Origin));
        await MotorsportTags.ApplyAsync(catalog, assetId, entries, Ct);
        await catalog.XmpSync.MarkPendingAsync(assetId, null, Ct);
        await publisher.WriteAsync([assetId], prudentMode: true, null, Ct);
        hierarchy = await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-lr:HierarchicalSubject");
        Assert.Contains("Motorsport|Numero 9", hierarchy, StringComparison.Ordinal);
        Assert.Contains("Motorsport|Pilota|Paolo Galli", hierarchy, StringComparison.Ordinal);
        Assert.DoesNotContain("Motorsport|Numero 27", hierarchy, StringComparison.Ordinal);   // the automatic numbers of this run replaced the others
    }

    public void Dispose() => _temp.Dispose();
}
