// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services;
using PhotoFlow.App.Services.Develop;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Ingest;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Data;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Metadata;

/// <summary>Author and rights (PhotoFlow Studio, M2): in the XMP at the confirmation, per event, and in the deliveries.</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class RightsUnitTests
{
    private static readonly RightsInfo Rights = new("Luca Rossi", "© {anno} Luca Rossi", "studio@example.com", "https://example.com", "Solo uso personale");

    [Fact]
    public void The_copyright_takes_the_year_of_the_photo()
    {
        Assert.Equal("© 2024 Luca Rossi", Rights.ForYear(2024).Copyright);
        Assert.Equal($"© {DateTime.Now.Year} Luca Rossi", Rights.ForYear(null).Copyright);
        Assert.True(new RightsInfo(null, " ").IsEmpty);
        Assert.False(Rights.IsEmpty);
    }

    [Fact]
    public void The_rights_become_iptc_fields_of_the_xmp()
    {
        var args = ExifToolMetadataWriter.TagArguments(new MetadataChanges(null, null, [], [], null, Rights: Rights.ForYear(2024)));
        Assert.Contains("-XMP-dc:Creator=Luca Rossi", args);
        Assert.Contains("-XMP-dc:Rights=© 2024 Luca Rossi", args);
        Assert.Contains("-XMP-xmpRights:Marked=True", args);
        Assert.Contains("-XMP-xmpRights:UsageTerms=Solo uso personale", args);
        Assert.Contains("-XMP-iptcCore:CreatorWorkEmail=studio@example.com", args);
        Assert.Contains("-XMP-iptcCore:CreatorWorkURL=https://example.com", args);
        Assert.Empty(ExifToolMetadataWriter.TagArguments(new MetadataChanges(null, null, [], [], null, Rights: new RightsInfo(null, null))));
    }

    [Fact]
    public void Each_import_keeps_the_rights_of_its_event()
    {
        var settings = new ImportJobSettings(1, "E:\\", [new IngestDestination("primary", "D:\\Foto", DestinationKind.Primary)], "events", Rights: Rights);
        var job = new Job(1, JobType.Import, "E:\\", "Matrimonio", null, "events", false, null, JobState.Completed, JobPriority.Normal,
            ImportJobRunner.SerializeSettings(settings), DateTimeOffset.UtcNow);
        Assert.Equal(Rights, JobRights.Of(job));
        Assert.Null(JobRights.Of(job with { SettingsSnapshotJson = ImportJobRunner.SerializeSettings(settings with { Rights = null }) }));
        Assert.Null(JobRights.Of(job with { Type = JobType.AnalyzeInPlace, SettingsSnapshotJson = "{}" }));
    }
}

[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class RightsXmpTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-rights");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_confirmation_writes_author_and_copyright_with_the_year_of_the_photo()
    {
        await using var exifTool = ExifToolFixture.Start();
        var root = _temp.Sub("library");
        var path = SyntheticImages.WriteJpeg(Path.Combine(root, "sposi.jpg"), 300, 200);
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.EmbeddedForNonRaw, false, LibraryOrigin.Import, null), Ct);
        var assetId = await catalog.Assets.AddAsync(new Asset(0, "q", "f", "JPG", "jpeg", 300, 200, new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero),
            DateSource.Exif, null, AssetState.Active), Ct);
        await catalog.Assets.AddLocationAsync(new AssetLocation(0, assetId, libraryId, "sposi.jpg", DateTimeOffset.UtcNow), Ct);
        var writer = new ExifToolMetadataWriter(exifTool, new PhysicalFileSystem());

        // No decision, nothing else to say: the rights alone are enough to write the XMP.
        var summary = await new XmpPublisher(catalog, writer, rights: new RightsInfo("Luca Rossi", "© {anno} Luca Rossi")).WriteAsync([assetId], prudentMode: true, null, Ct);
        Assert.Equal(1, summary.Written);
        Assert.Equal("Luca Rossi", (await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-dc:Creator")).Trim());
        Assert.Equal("© 2024 Luca Rossi", (await ExifToolFixture.ReadTagAsync(exifTool, path, "XMP-dc:Rights")).Trim());

        // Without rights a photo with nothing to say is not touched.
        var other = await catalog.Assets.AddAsync(new Asset(0, "q2", "f2", "JPG", "jpeg", 300, 200, null, DateSource.Unknown, null, AssetState.Active), Ct);
        Assert.Equal(0, (await new XmpPublisher(catalog, writer).WriteAsync([other], prudentMode: true, null, Ct)).Written);
    }

    [Fact]
    public async Task A_delivery_carries_rights_keywords_caption_and_stars()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var assetId = await catalog.Assets.AddAsync(new Asset(0, "q", "f", "JPG", "jpeg", 300, 200, new DateTimeOffset(2023, 7, 1, 12, 0, 0, TimeSpan.Zero),
            DateSource.Exif, null, AssetState.Active), Ct);
        await catalog.Tags.EnsureAsync("beach", TagSource.RamPlus, "Luoghi", "spiaggia", reviewed: true, Ct);
        await catalog.Tags.ReplaceAutomaticAsync(assetId, TagSource.RamPlus, null, [("beach", 0.9)], Ct);
        await catalog.Captions.SetManualAsync(assetId, "it", "La spiaggia al tramonto.", Ct);
        await catalog.Decisions.SetUserAsync(assetId, CullingOutcome.Kept, 5, null, Ct);

        var metadata = await DeliveryMetadata.ForAssetAsync(catalog, assetId, new RightsInfo("Luca Rossi", "© {anno} Luca Rossi"), Ct);
        Assert.Equal("Luca Rossi", (string?)metadata["creator"]);
        Assert.Equal("© 2023 Luca Rossi", (string?)metadata["rights"]);
        Assert.Contains("spiaggia", metadata["keywords"]!.AsArray().Select(k => (string?)k));
        Assert.Contains("Luoghi|Spiaggia", metadata["hierarchical"]!.AsArray().Select(k => (string?)k));
        Assert.Equal("La spiaggia al tramonto.", (string?)metadata["description"]);
        Assert.Equal(5, (int?)metadata["rating"]);
        Assert.False((await DeliveryMetadata.ForAssetAsync(catalog, assetId, null, Ct)).ContainsKey("creator"));
    }

    public void Dispose() => _temp.Dispose();
}
