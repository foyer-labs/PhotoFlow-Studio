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
/// Captions in XMP (spec §8): <c>dc:description</c> only when the preference is on; the user's caption replaces the
/// automatic one; read back with exiftool. RAW files only get sidecars (I2) as for everything else.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class CaptionXmpTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-caption-xmp");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Captions_go_in_the_description_only_when_asked()
    {
        await using var exifTool = ExifToolFixture.Start();
        var root = _temp.Sub("library");
        var path = SyntheticImages.WriteJpeg(Path.Combine(root, "track.jpg"), 300, 200);
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.EmbeddedForNonRaw, false, LibraryOrigin.Import, null), Ct);
        var assetId = await catalog.Assets.AddAsync(new Asset(0, "q", "f", "JPG", "jpeg", 300, 200, null, DateSource.Unknown, null, AssetState.Active), Ct);
        await catalog.Assets.AddLocationAsync(new AssetLocation(0, assetId, libraryId, "track.jpg", DateTimeOffset.UtcNow), Ct);
        await catalog.Decisions.SetUserAsync(assetId, CullingOutcome.Kept, 4, null, Ct);
        await catalog.Captions.SetAutomaticAsync(assetId, "en", "A red car on a race track.", "florence2-base-ft@1", Ct);
        var writer = new ExifToolMetadataWriter(exifTool, new PhysicalFileSystem());

        // Off (the default): no description.
        await new XmpPublisher(catalog, writer).WriteAsync([assetId], prudentMode: true, null, Ct);
        Assert.Equal(string.Empty, (await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-dc:Description")).Trim());

        // On: the automatic caption, then the user's.
        var publisher = new XmpPublisher(catalog, writer, writeCaptions: true);
        await publisher.WriteAsync([assetId], prudentMode: true, null, Ct);
        Assert.Equal("A red car on a race track.", (await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-dc:Description")).Trim());
        await catalog.Captions.SetManualAsync(assetId, "en", "Monza, la partenza.", Ct);
        await publisher.WriteAsync([assetId], prudentMode: true, null, Ct);
        Assert.Equal("Monza, la partenza.", (await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-dc:Description")).Trim());

        // Level D (phase 5b, D5b-6): the Italian caption is x-default, the English one an alternative language.
        await catalog.Captions.SetAutomaticAsync(assetId, "it", "Un'auto rossa in pista.", "qwen2.5-vl-7b@1", Ct);
        await publisher.WriteAsync([assetId], prudentMode: true, null, Ct);
        Assert.Equal("Un'auto rossa in pista.", (await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-dc:Description")).Trim());
        Assert.Equal("Monza, la partenza.", (await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-dc:Description-en")).Trim());
    }

    public void Dispose() => _temp.Dispose();
}
