// SPDX-License-Identifier: Apache-2.0

using ImageMagick;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Sources;
using SkiaSharp;

namespace PhotoFlow.Imaging;

/// <summary>JPEG decoding with DCT-domain downscaling (fast), exact final resize and EXIF orientation.</summary>
public static class JpegDecoder
{
    public static (RgbImage Image, int SourceWidth, int SourceHeight) Decode(ReadOnlySpan<byte> jpeg, int maxSize, int? orientationOverride = null)
    {
        using var data = SKData.CreateCopy(jpeg);
        using var codec = SKCodec.Create(data) ?? throw new ImageDecodeException("Unsupported or corrupt JPEG data.");
        var sourceWidth = codec.Info.Width;
        var sourceHeight = codec.Info.Height;

        // Smallest DCT scale (1, 1/2, 1/4, 1/8) whose long side is still >= maxSize.
        var target = codec.Info.Size;
        foreach (var scale in new[] { 0.125f, 0.25f, 0.5f })
        {
            var size = codec.GetScaledDimensions(scale);
            if (Math.Max(size.Width, size.Height) >= maxSize)
            {
                target = size;
                break;
            }
        }

        var info = new SKImageInfo(target.Width, target.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new ImageDecodeException($"JPEG decoding failed: {result}.");
        }

        var rgba = bitmap.GetPixelSpan();
        var rgb = new byte[info.Width * info.Height * 3];
        for (int i = 0, j = 0; j < rgb.Length; i += 4, j += 3)
        {
            rgb[j] = rgba[i];
            rgb[j + 1] = rgba[i + 1];
            rgb[j + 2] = rgba[i + 2];
        }

        var orientation = orientationOverride ?? (int)codec.EncodedOrigin;
        var image = ImageTransforms.ApplyOrientation(ImageTransforms.FitWithin(new RgbImage(info.Width, info.Height, rgb), maxSize), orientation);
        return orientation >= 5 ? (image, sourceHeight, sourceWidth) : (image, sourceWidth, sourceHeight);
    }
}

/// <summary>JPEG files (content-detected).</summary>
public sealed class JpegImageHandler(IFileSystem fileSystem) : IImageHandler
{
    public const string Id = "jpeg";

    public bool CanHandle(FileSignature sig) => MediaClassifier.Classify(sig.Header.Span, sig.Extension ?? string.Empty) == MediaKind.Jpeg;

    public async Task<PreviewImage> GetPreviewAsync(string path, int maxSize, CancellationToken ct)
    {
        byte[] bytes;
        await using (var stream = fileSystem.OpenRead(path))
        {
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        }

        var (image, width, height) = JpegDecoder.Decode(bytes, maxSize);
        return new PreviewImage(image, width, height, IsEmbeddedPreview: false);
    }
}

/// <summary>
/// RAW files: the largest embedded JPEG (fast, camera rendering); full decoding with LibRaw only when the preview is
/// missing or too small. Nikon High Efficiency NEFs are never given to LibRaw, which decodes them into a wrong image
/// without an error (R15): their preview is used whatever its size, and without one they are reported as undecodable.
/// </summary>
public sealed class RawImageHandler(IEmbeddedPreviewSource previews, IRawDecoder decoder) : IImageHandler
{
    public const string Id = "raw";

    /// <summary>Below this long side an embedded preview is considered too small for analysis.</summary>
    public const int MinimumUsablePreview = 1024;

    public bool CanHandle(FileSignature sig) => MediaClassifier.Classify(sig.Header.Span, sig.Extension ?? string.Empty) == MediaKind.Raw;

    public async Task<PreviewImage> GetPreviewAsync(string path, int maxSize, CancellationToken ct)
    {
        PreviewImage? fromPreview = null;
        var embedded = await previews.ExtractLargestAsync(path, ct).ConfigureAwait(false);
        if (embedded.Largest is { } preview)
        {
            try
            {
                var (image, width, height) = JpegDecoder.Decode(preview.Jpeg, maxSize, preview.Orientation);
                fromPreview = new PreviewImage(image, width, height, IsEmbeddedPreview: true);
                if (!embedded.FullDecodingSupported || Math.Max(width, height) >= Math.Min(maxSize, MinimumUsablePreview))
                {
                    return fromPreview;
                }
            }
            catch (ImageDecodeException)
            {
                // Damaged preview: try the full decoding.
            }
        }

        if (!embedded.FullDecodingSupported)
        {
            throw new ImageDecodeException("Nikon High Efficiency NEF without a readable embedded preview: its RAW data cannot be decoded.");
        }

        try
        {
            var decoded = await decoder.DecodeAsync(path, maxSize, ct).ConfigureAwait(false);
            return new PreviewImage(decoded, decoded.Width, decoded.Height, IsEmbeddedPreview: false);
        }
        catch (ImageDecodeException) when (fromPreview is not null)
        {
            return fromPreview;
        }
    }
}

/// <summary>Full RAW decoding through Magick.NET (LibRaw 0.22.x), decision F1-3.</summary>
public sealed class MagickRawDecoder : IRawDecoder
{
    public Task<RgbImage> DecodeAsync(string rawPath, int maxSize, CancellationToken ct) =>
        Task.Run(
            () =>
            {
                try
                {
                    using var image = new MagickImage(rawPath);
                    ct.ThrowIfCancellationRequested();
                    image.AutoOrient();
                    if (Math.Max(image.Width, image.Height) > maxSize)
                    {
                        image.Resize(new MagickGeometry((uint)maxSize, (uint)maxSize));
                    }

                    image.Depth = 8; // decoded RAWs are 16 bit per channel
                    var pixels = image.ToByteArray(MagickFormat.Rgb);
                    return new RgbImage((int)image.Width, (int)image.Height, pixels);
                }
                catch (MagickException e)
                {
                    throw new ImageDecodeException($"RAW decoding failed: {e.Message}", e);
                }
            },
            ct);
}

/// <summary>Picks the handler from the file content (spec §3).</summary>
public sealed class ImageHandlerRegistry(IEnumerable<IImageHandler> handlers)
{
    private readonly IReadOnlyList<IImageHandler> _handlers = handlers.ToList();

    public IImageHandler? Find(FileSignature signature) => _handlers.FirstOrDefault(h => h.CanHandle(signature));

    /// <summary>All the formats of PhotoFlow: RAW, JPEG, PNG/WebP, TIFF/HEIF.</summary>
    public static ImageHandlerRegistry CreateDefault(IFileSystem fileSystem, IEmbeddedPreviewSource previews, IRawDecoder rawDecoder) =>
        new([new RawImageHandler(previews, rawDecoder), new JpegImageHandler(fileSystem), new SkiaImageHandler(fileSystem), new MagickImageHandler(fileSystem)]);
}

/// <summary>Reads the first bytes of the file, picks the handler and returns the upright preview.</summary>
public sealed class PreviewProvider(IFileSystem fileSystem, ImageHandlerRegistry handlers) : IPreviewProvider
{
    public async Task<PreviewImage> GetPreviewAsync(string path, int maxSize, CancellationToken ct)
    {
        var header = new byte[MediaClassifier.HeaderLength];
        int read;
        await using (var stream = fileSystem.OpenRead(path))
        {
            read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        }

        var handler = handlers.Find(new FileSignature(header.AsMemory(0, read), Path.GetExtension(path)))
            ?? throw new ImageDecodeException($"No image handler for '{Path.GetFileName(path)}'.");
        return await handler.GetPreviewAsync(path, maxSize, ct).ConfigureAwait(false);
    }
}

/// <summary>PNG and WebP (content-detected), decoded by SkiaSharp with the EXIF orientation when present.</summary>
public sealed class SkiaImageHandler(IFileSystem fileSystem) : IImageHandler
{
    public const string Id = "skia";

    public bool CanHandle(FileSignature sig) =>
        MediaClassifier.Classify(sig.Header.Span, sig.Extension ?? string.Empty) is MediaKind.Png or MediaKind.WebP;

    public async Task<PreviewImage> GetPreviewAsync(string path, int maxSize, CancellationToken ct)
    {
        byte[] bytes;
        await using (var stream = fileSystem.OpenRead(path))
        {
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        }

        return await Task.Run(() => Decode(bytes, maxSize), ct).ConfigureAwait(false);
    }

    private static PreviewImage Decode(byte[] encoded, int maxSize)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data) ?? throw new ImageDecodeException("Unsupported or corrupt PNG/WebP data.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            // A truncated file is corrupt: reported, never analyzed as a partial image.
            throw new ImageDecodeException($"Image decoding failed: {result}.");
        }

        var rgba = bitmap.GetPixelSpan();
        var rgb = new byte[info.Width * info.Height * 3];
        for (int i = 0, j = 0; j < rgb.Length; i += 4, j += 3)
        {
            // Transparent areas become white (a photo viewer shows them on a light background).
            var a = rgba[i + 3];
            rgb[j] = (byte)(((rgba[i] * a) + (255 * (255 - a))) / 255);
            rgb[j + 1] = (byte)(((rgba[i + 1] * a) + (255 * (255 - a))) / 255);
            rgb[j + 2] = (byte)(((rgba[i + 2] * a) + (255 * (255 - a))) / 255);
        }

        var orientation = (int)codec.EncodedOrigin;
        var image = ImageTransforms.ApplyOrientation(ImageTransforms.FitWithin(new RgbImage(info.Width, info.Height, rgb), maxSize), orientation);
        var (width, height) = orientation >= 5 ? (info.Height, info.Width) : (info.Width, info.Height);
        return new PreviewImage(image, width, height, IsEmbeddedPreview: false);
    }
}

/// <summary>
/// TIFF (8/16 bit, multipage: first page) and HEIF/HEIC/AVIF, decoded by Magick.NET (libtiff, libheif) with the
/// orientation applied and 8 bits per channel. Damaged files raise <see cref="ImageDecodeException"/>.
/// </summary>
public sealed class MagickImageHandler(IFileSystem fileSystem) : IImageHandler
{
    public const string Id = "magick";

    public bool CanHandle(FileSignature sig) =>
        MediaClassifier.Classify(sig.Header.Span, sig.Extension ?? string.Empty) is MediaKind.Tiff or MediaKind.Heif;

    public async Task<PreviewImage> GetPreviewAsync(string path, int maxSize, CancellationToken ct)
    {
        byte[] bytes;
        await using (var stream = fileSystem.OpenRead(path))
        {
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        }

        return await Task.Run(
            () =>
            {
                try
                {
                    using var image = new MagickImage(bytes, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
                    ct.ThrowIfCancellationRequested();
                    image.AutoOrient();
                    var (width, height) = ((int)image.Width, (int)image.Height);
                    if (Math.Max(image.Width, image.Height) > maxSize)
                    {
                        image.Resize(new MagickGeometry((uint)maxSize, (uint)maxSize));
                    }

                    image.BackgroundColor = MagickColors.White;
                    image.Alpha(AlphaAction.Remove);
                    image.ColorSpace = ColorSpace.sRGB;
                    image.Depth = 8;
                    var pixels = image.ToByteArray(MagickFormat.Rgb);
                    return new PreviewImage(new RgbImage((int)image.Width, (int)image.Height, pixels), width, height, IsEmbeddedPreview: false);
                }
                catch (MagickException e)
                {
                    throw new ImageDecodeException($"Image decoding failed: {e.Message}", e);
                }
            },
            ct).ConfigureAwait(false);
    }
}
