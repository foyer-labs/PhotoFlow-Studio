// SPDX-License-Identifier: Apache-2.0

using ImageMagick;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.IO;
using PhotoFlow.Imaging;
using PhotoFlow.Tests.Support;
using SkiaSharp;

namespace PhotoFlow.Tests.Imaging;

/// <summary>
/// Phase 1b formats (spec §4, §13): PNG and WebP through SkiaSharp, TIFF (16 bit, multipage) and HEIC through
/// Magick.NET; the handler comes from the content, so a wrong extension changes nothing; damaged files raise a
/// decoding error and stay intact.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class FormatHandlerTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-formats");
    private readonly PreviewProvider _previews;

    public FormatHandlerTests()
    {
        var fs = new PhysicalFileSystem();
        _previews = new PreviewProvider(fs, new ImageHandlerRegistry([new JpegImageHandler(fs), new SkiaImageHandler(fs), new MagickImageHandler(fs)]));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Formats => new()
    {
        { "png", ".png" },
        { "png-alpha", ".png" },
        { "webp", ".webp" },
        { "tiff16", ".tif" },
        { "tiff-multipage", ".tiff" },
        { "heic", ".heic" },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Every_format_is_decoded_to_an_upright_8_bit_preview(string kind, string extension)
    {
        var path = Write(kind, "photo" + extension);
        var preview = await _previews.GetPreviewAsync(path, 2048, Ct);
        Assert.Equal(96, preview.SourceWidth);
        Assert.Equal(64, preview.SourceHeight);
        Assert.Equal(96 * 64 * 3, preview.Image.Pixels.Length);
        // Left edge dark red channel, right edge bright: the gradient survived decoding (orientation kept).
        var px = preview.Image.Pixels.Span;
        var row = 32 * 96 * 3;
        Assert.True(px[row + (95 * 3)] > px[row] + 100, $"{kind}: left {px[row]} right {px[row + (95 * 3)]}");
    }

    [Theory]
    [InlineData("jpeg", ".png")]
    [InlineData("tiff16", ".jpg")]
    [InlineData("heic", ".jpg")]
    [InlineData("png", ".tif")]
    public async Task A_wrong_extension_still_reaches_the_right_handler(string kind, string wrongExtension)
    {
        var path = Write(kind, "renamed" + wrongExtension);
        var preview = await _previews.GetPreviewAsync(path, 2048, Ct);
        Assert.Equal(96, preview.SourceWidth);
    }

    [Theory]
    [InlineData("jpeg", ".jpg")]
    [InlineData("png", ".png")]
    [InlineData("webp", ".webp")]
    [InlineData("tiff16", ".tif")]
    [InlineData("heic", ".heic")]
    public async Task A_truncated_file_raises_a_decoding_error_and_stays_intact(string kind, string extension)
    {
        var path = Write(kind, "broken" + extension);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 3)]);
        var before = FolderSnapshot.Sha256(path);

        await Assert.ThrowsAsync<ImageDecodeException>(() => _previews.GetPreviewAsync(path, 2048, Ct));

        Assert.Equal(before, FolderSnapshot.Sha256(path));
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_rejected_by_content()
    {
        var path = Path.Combine(_temp.Path, "notes.jpg");
        await File.WriteAllTextAsync(path, "not an image at all", Ct);
        await Assert.ThrowsAsync<ImageDecodeException>(() => _previews.GetPreviewAsync(path, 2048, Ct));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A 96×64 horizontal gradient in the requested format (HEIC: the public CC0 sample).</summary>
    private string Write(string kind, string name)
    {
        var path = Path.Combine(_temp.Path, name);
        if (kind == "heic")
        {
            File.Copy(Repository.PathOf("tests", "data", "public", "synthetic-gradient.heic"), path);
            return path;
        }

        if (kind is "tiff16" or "tiff-multipage")
        {
            using var collection = new MagickImageCollection();
            var pages = kind == "tiff-multipage" ? 3 : 1;
            for (var p = 0; p < pages; p++)
            {
                var image = new MagickImage("gradient:black-red", 64, 96);
                image.Rotate(-90); // left → right
                image.Depth = 16;
                collection.Add(image);
            }

            collection.Write(path, MagickFormat.Tiff);
            return path;
        }

        using var bitmap = new SKBitmap(96, 64, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 96; x++)
            {
                var alpha = kind == "png-alpha" && y < 8 ? (byte)0 : (byte)255;
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 255 / 95), 40, 40, alpha));
            }
        }

        var format = kind switch
        {
            "png" or "png-alpha" => SKEncodedImageFormat.Png,
            "webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Jpeg,
        };
        using var data = bitmap.Encode(format, 95);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }
}
