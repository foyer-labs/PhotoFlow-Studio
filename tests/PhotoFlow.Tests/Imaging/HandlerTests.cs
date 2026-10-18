// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.IO;
using PhotoFlow.Imaging;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Imaging;

[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class RawHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("nikon-d40-lossy", 3008, 2000)]
    [InlineData("nikon-d5100-lossless", 4928, 3264)]
    [InlineData("nikon-z8-high-efficiency", 8256, 5504)]
    public async Task Raw_previews_come_from_the_full_size_embedded_jpeg(string sample, int width, int height)
    {
        await using var exifTool = ExifToolFixture.Start();
        var raw = await PublicSamples.RequireAsync(sample);
        var handler = new RawImageHandler(new ExifToolPreviewSource(exifTool), new MagickRawDecoder());

        var preview = await handler.GetPreviewAsync(raw, 1280, Ct);

        Assert.True(preview.IsEmbeddedPreview);
        Assert.Equal((width, height), (preview.SourceWidth, preview.SourceHeight));
        Assert.Equal(1280, Math.Max(preview.Image.Width, preview.Image.Height));
        Assert.InRange(MeanLuma(preview.Image), 10, 245); // a real picture, not a blank or black frame
    }

    [Fact]
    public async Task Without_a_preview_the_raw_is_fully_decoded()
    {
        var raw = await PublicSamples.RequireAsync("nikon-d40-lossy");
        var handler = new RawImageHandler(new NoPreviews(), new MagickRawDecoder());

        var preview = await handler.GetPreviewAsync(raw, 800, Ct);

        Assert.False(preview.IsEmbeddedPreview);
        Assert.Equal(800, Math.Max(preview.Image.Width, preview.Image.Height));
        Assert.InRange(MeanLuma(preview.Image), 10, 245);
    }

    [Fact]
    public async Task A_corrupt_raw_without_preview_raises_a_decode_error()
    {
        using var temp = new TempFolder("pf-bad");
        var bad = Path.Combine(temp.Path, "broken.NEF");
        var bytes = new byte[50_000];
        new Random(3).NextBytes(bytes);
        new byte[] { 0x4D, 0x4D, 0x00, 0x2A }.CopyTo(bytes, 0);
        await File.WriteAllBytesAsync(bad, bytes, Ct);

        await Assert.ThrowsAsync<ImageDecodeException>(() => new RawImageHandler(new NoPreviews(), new MagickRawDecoder()).GetPreviewAsync(bad, 800, Ct));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(bad, Ct)); // reported, never modified
    }

    [Theory]
    [InlineData("nikon-z8-high-efficiency", false)]
    [InlineData("nikon-d5100-lossless", true)]
    public async Task The_preview_source_tells_whether_the_raw_data_can_be_decoded(string sample, bool decodable)
    {
        await using var exifTool = ExifToolFixture.Start();
        var raw = await PublicSamples.RequireAsync(sample);

        var previews = await new ExifToolPreviewSource(exifTool).ExtractLargestAsync(raw, Ct);

        Assert.NotNull(previews.Largest);
        Assert.Equal(decodable, previews.FullDecodingSupported);
    }

    [Fact]
    public async Task A_high_efficiency_nef_keeps_even_a_small_preview_and_never_reaches_libraw()
    {
        // LibRaw turns High Efficiency NEFs into stripes without an error (Z5 II, 14 Oct 2026): its picture must never be used.
        using var temp = new TempFolder("pf-he");
        var jpeg = await File.ReadAllBytesAsync(SyntheticImages.WriteJpeg(Path.Combine(temp.Path, "preview.jpg"), 640, 424), Ct);
        var decoder = new CountingDecoder();
        var handler = new RawImageHandler(new FixedPreviews(new EmbeddedPreviews(new EmbeddedPreview(jpeg, "PreviewImage", 1), FullDecodingSupported: false)), decoder);

        var preview = await handler.GetPreviewAsync(Path.Combine(temp.Path, "DSC_0001.NEF"), 1280, Ct);

        Assert.True(preview.IsEmbeddedPreview);
        Assert.Equal((640, 424), (preview.Image.Width, preview.Image.Height));
        Assert.Equal(0, decoder.Calls);
    }

    [Fact]
    public async Task A_high_efficiency_nef_without_preview_is_reported_not_decoded()
    {
        var decoder = new CountingDecoder();
        var handler = new RawImageHandler(new FixedPreviews(new EmbeddedPreviews(null, FullDecodingSupported: false)), decoder);

        await Assert.ThrowsAsync<ImageDecodeException>(() => handler.GetPreviewAsync("DSC_0001.NEF", 800, Ct));
        Assert.Equal(0, decoder.Calls);
    }

    private static double MeanLuma(RgbImage image)
    {
        var p = image.Pixels.Span;
        double sum = 0;
        for (var i = 0; i < p.Length; i += 3)
        {
            sum += 0.299 * p[i] + 0.587 * p[i + 1] + 0.114 * p[i + 2];
        }

        return sum / (p.Length / 3);
    }

    private sealed class NoPreviews : IEmbeddedPreviewSource
    {
        public Task<EmbeddedPreviews> ExtractLargestAsync(string rawPath, CancellationToken ct) => Task.FromResult(new EmbeddedPreviews(null));
    }

    private sealed class FixedPreviews(EmbeddedPreviews previews) : IEmbeddedPreviewSource
    {
        public Task<EmbeddedPreviews> ExtractLargestAsync(string rawPath, CancellationToken ct) => Task.FromResult(previews);
    }

    private sealed class CountingDecoder : IRawDecoder
    {
        public int Calls { get; private set; }

        public Task<RgbImage> DecodeAsync(string rawPath, int maxSize, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new RgbImage(2, 2, new byte[12]));
        }
    }
}

[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class JpegHandlerTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-jpg");

    [Fact]
    public async Task Exif_orientation_is_applied()
    {
        await using var exifTool = ExifToolFixture.Start();
        var jpeg = SyntheticImages.WriteJpeg(Path.Combine(_temp.Path, "rotated.jpg"), width: 300, height: 200);
        await exifTool.ExecuteAsync(["-overwrite_original", "-Orientation#=6", jpeg], TestContext.Current.CancellationToken);

        var preview = await new JpegImageHandler(new PhysicalFileSystem()).GetPreviewAsync(jpeg, 150, TestContext.Current.CancellationToken);

        Assert.Equal((200, 300), (preview.SourceWidth, preview.SourceHeight));
        Assert.Equal((100, 150), (preview.Image.Width, preview.Image.Height));
    }

    public void Dispose() => _temp.Dispose();
}

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class OrientationTests
{
    // 3×2 image, pixel value = index (row-major); R channel carries it.
    private static readonly RgbImage Source = new(3, 2, Enumerable.Range(0, 6).SelectMany(i => new[] { (byte)i, (byte)0, (byte)0 }).ToArray());

    [Theory]
    [InlineData(1, 3, 2, new byte[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(2, 3, 2, new byte[] { 2, 1, 0, 5, 4, 3 })]
    [InlineData(3, 3, 2, new byte[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(4, 3, 2, new byte[] { 3, 4, 5, 0, 1, 2 })]
    [InlineData(5, 2, 3, new byte[] { 0, 3, 1, 4, 2, 5 })]
    [InlineData(6, 2, 3, new byte[] { 3, 0, 4, 1, 5, 2 })]
    [InlineData(7, 2, 3, new byte[] { 5, 2, 4, 1, 3, 0 })]
    [InlineData(8, 2, 3, new byte[] { 2, 5, 1, 4, 0, 3 })]
    public void Every_exif_orientation_maps_pixels_correctly(int orientation, int width, int height, byte[] expected)
    {
        var result = ImageTransforms.ApplyOrientation(Source, orientation);
        Assert.Equal((width, height), (result.Width, result.Height));
        Assert.Equal(expected, result.Pixels.ToArray().Where((_, i) => i % 3 == 0));
    }
}

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class ThumbnailCacheTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-thumb");

    [Fact]
    public async Task Thumbnails_are_stored_once_by_content_hash_and_decode_back()
    {
        var cache = new ThumbnailCache(new PhysicalFileSystem(), _temp.Path);
        var image = new RgbImage(1600, 1000, Enumerable.Repeat((byte)120, 1600 * 1000 * 3).ToArray());

        var path = await cache.StoreAsync("abcdef0123456789", image, TestContext.Current.CancellationToken);
        var again = await cache.StoreAsync("abcdef0123456789", image, TestContext.Current.CancellationToken);

        Assert.Equal(path, again);
        Assert.True(cache.Exists("abcdef0123456789"));
        var (decoded, width, height) = JpegDecoder.Decode(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken), 1000);
        Assert.Equal((512, 320), (width, height));
        Assert.Equal((512, 320), (decoded.Width, decoded.Height));
        Assert.Empty(Directory.EnumerateFiles(_temp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    public void Dispose() => _temp.Dispose();
}
