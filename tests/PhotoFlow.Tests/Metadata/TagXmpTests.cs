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
/// Tags in XMP (spec §8, acceptance of phase 3 "Keyword IT+EN e gerarchiche"): English always, Italian once reviewed,
/// <c>Category|Label</c> in the hierarchy, blacklisted never; a tag the user removes later is taken out of the file,
/// while keywords written by other programs stay, even when they are the same word.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class TagXmpTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-tag-xmp");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tags_are_written_in_both_languages_and_removed_ones_go_away_without_touching_others()
    {
        await using var exifTool = ExifToolFixture.Start();
        var root = _temp.Sub("library");
        var path = SyntheticImages.WriteJpeg(Path.Combine(root, "lake.jpg"), 300, 200);

        // Another program already wrote two keywords: one of them is also the English key of a PhotoFlow tag.
        await exifTool.ExecuteAsync(["-overwrite_original", "-XMP-dc:Subject=vacanze", "-XMP-dc:Subject+=beach", path], Ct);

        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.EmbeddedForNonRaw, false, LibraryOrigin.Import, null), Ct);
        var assetId = await catalog.Assets.AddAsync(new Asset(0, "q", "f", "JPG", "jpeg", 300, 200, null, DateSource.Unknown, null, AssetState.Active), Ct);
        await catalog.Assets.AddLocationAsync(new AssetLocation(0, assetId, libraryId, "lake.jpg", DateTimeOffset.UtcNow), Ct);
        var tags = catalog.Tags;
        await tags.SeedVocabularyAsync([new("sunset", "tramonto", "Natura"), new("lake", "lago", "Natura"), new("image", "immagine", null)], TagSource.RamPlus, Ct);
        var sunset = (await tags.FindAsync("sunset", Ct))!;
        await tags.UpdateAsync(sunset.Id, "tramonto", reviewed: true, "Natura", blacklisted: false, Ct);   // reviewed: Italian written
        var image = (await tags.FindAsync("image", Ct))!;
        await tags.UpdateAsync(image.Id, "immagine", reviewed: false, null, blacklisted: true, Ct);        // blacklisted: never written
        await tags.ReplaceAutomaticAsync(assetId, TagSource.RamPlus, "ram@1", [("sunset", 0.9), ("lake", 0.8), ("image", 0.99)], Ct);
        await tags.EnsureAsync("event:Gita al lago", TagSource.Job, "Eventi", "Gita al lago", true, Ct);
        await tags.ReplaceAutomaticAsync(assetId, TagSource.Job, null, [("event:Gita al lago", null)], Ct);
        await catalog.Decisions.SetUserAsync(assetId, CullingOutcome.Kept, 3, null, Ct);

        var publisher = new XmpPublisher(catalog, new ExifToolMetadataWriter(exifTool, new PhysicalFileSystem()));
        Assert.Equal(1, (await publisher.WriteAsync([assetId], prudentMode: true, null, Ct)).Written);

        var subject = await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-dc:Subject");
        var hierarchy = await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-lr:HierarchicalSubject");
        foreach (var expected in new[] { "sunset", "tramonto", "lake", "Gita al lago", "vacanze", "beach" })
        {
            Assert.Contains(expected, subject, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("lago", subject.Split('|'));   // not reviewed: English only
        Assert.Contains("lake", subject.Split('|'));
        Assert.DoesNotContain("image", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("immagine", subject, StringComparison.Ordinal);
        foreach (var expected in new[] { "Natura|Tramonto", "Natura|Lake", "Eventi|Gita al lago" })
        {
            Assert.Contains(expected, hierarchy, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Natura|Sunset", hierarchy, StringComparison.Ordinal);   // the hierarchy is in Italian once reviewed

        // The user removes "sunset"; another analysis finds the lake no more. Only PhotoFlow's keywords go away.
        await tags.SuppressAsync(assetId, sunset.Id, Ct);
        await tags.ReplaceAutomaticAsync(assetId, TagSource.RamPlus, "ram@2", [("beach", 0.7)], Ct);
        await publisher.WriteAsync([assetId], prudentMode: true, null, Ct);
        subject = await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-dc:Subject");
        hierarchy = await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-lr:HierarchicalSubject");
        Assert.DoesNotContain("sunset", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("tramonto", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("lake", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("Natura|Tramonto", hierarchy, StringComparison.Ordinal);
        Assert.Contains("vacanze", subject, StringComparison.Ordinal);
        Assert.Contains("beach", subject, StringComparison.Ordinal);
        Assert.Contains("Gita al lago", subject, StringComparison.Ordinal);
    }

    public void Dispose() => _temp.Dispose();
}
