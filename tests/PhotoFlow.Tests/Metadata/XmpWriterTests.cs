// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Metadata;

/// <summary>XMP written by PhotoFlow is read back correctly by exiftool (phase 1 acceptance criterion).</summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class XmpWriterTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-xmp");

    private static MetadataChanges Changes(int? rating = 3, string? label = "Red", params string[] keywords) =>
        new(rating, label, keywords, [], null);

    [Fact]
    public async Task Embedded_xmp_in_a_jpeg_is_read_back_and_the_pixels_are_unchanged()
    {
        await using var exifTool = ExifToolFixture.Start();
        var jpeg = SyntheticImages.WriteJpeg(Path.Combine(_temp.Path, "photo.jpg"));
        var pixelsBefore = await ExifToolFixture.ReadTagAsync(exifTool, jpeg, "ImageDataHash");
        var writer = new ExifToolMetadataWriter(exifTool, new PhysicalFileSystem());

        var result = await writer.WriteAsync(new MetadataTarget(jpeg, IsRaw: false), Changes(4, "Red", "pista", "auto"), WritePolicy.EmbeddedForNonRaw, TestContext.Current.CancellationToken);

        Assert.Equal(MetadataWriteOutcome.Embedded, result.Outcome);
        Assert.Equal("4", await ExifToolFixture.ReadTagAsync(exifTool, jpeg, "XMP-xmp:Rating"));
        Assert.Equal("Red", await ExifToolFixture.ReadTagAsync(exifTool, jpeg, "XMP-xmp:Label"));
        Assert.Equal("pista|auto", await ExifToolFixture.ReadTagAsync(exifTool, jpeg, "XMP-dc:Subject"));
        Assert.Equal(pixelsBefore, await ExifToolFixture.ReadTagAsync(exifTool, jpeg, "ImageDataHash"));
        Assert.True(File.Exists(jpeg + "_original"), "The prudent default keeps the original copy.");
        Assert.False(File.Exists(ExifToolMetadataWriter.SidecarPath(jpeg)));
    }

    [Fact]
    public async Task Keywords_are_merged_never_duplicated_and_the_first_backup_is_kept()
    {
        await using var exifTool = ExifToolFixture.Start();
        var jpeg = SyntheticImages.WriteJpeg(Path.Combine(_temp.Path, "photo.jpg"));
        var original = FolderSnapshot.Sha256(jpeg);
        var writer = new ExifToolMetadataWriter(exifTool, new PhysicalFileSystem());

        await writer.WriteAsync(new MetadataTarget(jpeg, false), Changes(2, null, "mare"), WritePolicy.EmbeddedForNonRaw, TestContext.Current.CancellationToken);
        await writer.WriteAsync(new MetadataTarget(jpeg, false), Changes(5, null, "mare", "tramonto"), WritePolicy.EmbeddedForNonRaw, TestContext.Current.CancellationToken);

        Assert.Equal("mare|tramonto", await ExifToolFixture.ReadTagAsync(exifTool, jpeg, "XMP-dc:Subject"));
        Assert.Equal("5", await ExifToolFixture.ReadTagAsync(exifTool, jpeg, "XMP-xmp:Rating"));
        Assert.Equal(original, FolderSnapshot.Sha256(jpeg + "_original"));
    }

    [Fact]
    public async Task Sidecar_only_policy_leaves_the_jpeg_untouched()
    {
        await using var exifTool = ExifToolFixture.Start();
        var jpeg = SyntheticImages.WriteJpeg(Path.Combine(_temp.Path, "photo.jpg"));
        var before = FolderSnapshot.Sha256(jpeg);
        var writer = new ExifToolMetadataWriter(exifTool, new PhysicalFileSystem());

        var result = await writer.WriteAsync(new MetadataTarget(jpeg, false), Changes(1), WritePolicy.SidecarOnly, TestContext.Current.CancellationToken);

        Assert.Equal(MetadataWriteOutcome.Sidecar, result.Outcome);
        Assert.Equal(before, FolderSnapshot.Sha256(jpeg));
        Assert.Equal("1", await ExifToolFixture.ReadTagAsync(exifTool, ExifToolMetadataWriter.SidecarPath(jpeg), "XMP-xmp:Rating"));
    }

    [Fact]
    public async Task Existing_sidecar_from_another_program_is_updated_not_replaced()
    {
        await using var exifTool = ExifToolFixture.Start();
        var raw = await PublicSamples.RequireAsync("nikon-d40-lossy");
        var copy = Path.Combine(_temp.Path, "DSC_0001.NEF");
        File.Copy(raw, copy);
        var sidecar = ExifToolMetadataWriter.SidecarPath(copy);
        await exifTool.ExecuteAsync(["-o", sidecar, "-XMP-dc:Subject=Lightroom keyword", "-XMP-dc:Creator=Luca", copy], TestContext.Current.CancellationToken);
        var writer = new ExifToolMetadataWriter(exifTool, new PhysicalFileSystem());

        await writer.WriteAsync(new MetadataTarget(copy, true), Changes(3, "Red", "pista"), WritePolicy.EmbeddedForNonRaw, TestContext.Current.CancellationToken);

        Assert.Equal("Lightroom keyword|pista", await ExifToolFixture.ReadTagAsync(exifTool, sidecar, "XMP-dc:Subject"));
        Assert.Equal("Luca", await ExifToolFixture.ReadTagAsync(exifTool, sidecar, "XMP-dc:Creator"));
        Assert.Equal("3", await ExifToolFixture.ReadTagAsync(exifTool, sidecar, "XMP-xmp:Rating"));
    }

    [Fact]
    public void Tag_arguments_merge_keywords_and_validate_rating()
    {
        var args = ExifToolMetadataWriter.TagArguments(new MetadataChanges(-1, "Red", ["a", "a", "b"], ["Persone|Sara"], "riga1\nriga2"));
        Assert.Equal(
            ["-XMP-xmp:Rating=-1", "-XMP-xmp:Label=Red", "-XMP-dc:Subject-=a", "-XMP-dc:Subject+=a", "-XMP-dc:Subject-=b", "-XMP-dc:Subject+=b",
             "-XMP-lr:HierarchicalSubject-=Persone|Sara", "-XMP-lr:HierarchicalSubject+=Persone|Sara", "-XMP-dc:Description=riga1 riga2"],
            args);
        Assert.Throws<ArgumentOutOfRangeException>(() => ExifToolMetadataWriter.TagArguments(new MetadataChanges(6, null, [], [], null)));
    }

    public void Dispose() => _temp.Dispose();
}
