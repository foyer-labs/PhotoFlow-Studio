// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Invariants;

/// <summary>I2: RAW files are never modified; metadata goes to an XMP sidecar only.</summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class RawIsNeverModifiedTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-raw");

    public static TheoryData<string, WritePolicy> Cases => new()
    {
        { "nikon-d40-lossy", WritePolicy.SidecarOnly },
        { "nikon-d40-lossy", WritePolicy.EmbeddedForNonRaw },
        { "nikon-d40-lossy", WritePolicy.CatalogOnly },
        { "nikon-d5100-lossless", WritePolicy.EmbeddedForNonRaw },
        { "nikon-z8-high-efficiency", WritePolicy.EmbeddedForNonRaw },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Writing_metadata_leaves_the_raw_file_byte_identical(string sample, WritePolicy policy)
    {
        var source = await PublicSamples.RequireAsync(sample);
        var raw = Path.Combine(_temp.Path, Path.GetFileName(source));
        File.Copy(source, raw);
        var before = FolderSnapshot.Sha256(raw);
        await using var exifTool = ExifToolFixture.Start();
        var writer = new ExifToolMetadataWriter(exifTool, new PhysicalFileSystem());

        var result = await writer.WriteAsync(
            new MetadataTarget(raw, IsRaw: true),
            new MetadataChanges(Rating: 3, Label: "Red", FlatKeywords: [], HierarchicalKeywords: [], Description: null),
            policy,
            TestContext.Current.CancellationToken);

        Assert.Equal(before, FolderSnapshot.Sha256(raw));
        var sidecar = Path.ChangeExtension(raw, ".xmp");
        if (policy == WritePolicy.CatalogOnly)
        {
            Assert.Equal(MetadataWriteOutcome.CatalogOnly, result.Outcome);
            Assert.False(File.Exists(sidecar));
        }
        else
        {
            Assert.Equal(MetadataWriteOutcome.Sidecar, result.Outcome);
            Assert.True(File.Exists(sidecar), "The XMP sidecar must exist next to the RAW file.");
        }

        Assert.Equal([raw, .. File.Exists(sidecar) ? new[] { sidecar } : []], Directory.GetFiles(_temp.Path).Order(StringComparer.Ordinal));
    }

    public void Dispose() => _temp.Dispose();
}
