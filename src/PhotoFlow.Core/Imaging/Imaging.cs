// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Core.Imaging;

/// <summary>Format handler chosen from the file content, not from the extension (spec §3, §4).</summary>
public interface IImageHandler
{
    bool CanHandle(FileSignature sig);

    Task<PreviewImage> GetPreviewAsync(string path, int maxSize, CancellationToken ct);
}

/// <summary>Upright preview of a file, whatever its format: the handler is chosen from the content (spec §3).</summary>
public interface IPreviewProvider
{
    /// <exception cref="ImageDecodeException">No handler for the content, or the file cannot be decoded.</exception>
    Task<PreviewImage> GetPreviewAsync(string path, int maxSize, CancellationToken ct);
}

/// <summary>Grid thumbnails keyed by the content hash (local, rebuildable cache, spec §9).</summary>
public interface IThumbnailStore
{
    bool Exists(string contentHash);

    /// <summary>Where the thumbnail of this content is (it may not exist).</summary>
    string PathFor(string contentHash);

    Task<string> StoreAsync(string contentHash, RgbImage preview, CancellationToken ct);
}

/// <summary>Image decoding and resampling; isolates the imaging library.</summary>
public interface IImageOperations
{
    Task<RgbImage> DecodeAsync(Stream stream, CancellationToken ct);

    RgbImage Resize(RgbImage image, int width, int height, ResampleMode mode);

    /// <summary>Resamples the fractional source <paramref name="box"/> (Pillow <c>box</c> semantics) to the given size.</summary>
    RgbImage Resize(RgbImage image, SourceBox box, int width, int height, ResampleMode mode);

    /// <summary>The image as a JPEG file (to hand it to another process, e.g. the level D captions).</summary>
    byte[] EncodeJpeg(RgbImage image, int quality);
}

/// <summary>Fractional source rectangle in pixels (single precision, as Pillow's <c>box</c>).</summary>
public readonly record struct SourceBox(float X0, float Y0, float X1, float Y1);

public enum ResampleMode
{
    /// <summary>Triangle filter with support scaled on downsampling (Pillow BILINEAR semantics).</summary>
    Bilinear,

    /// <summary>Cubic filter (a = −0.5) with support scaled on downsampling (Pillow BICUBIC semantics, CLIP preprocessing).</summary>
    Bicubic,
}

/// <summary>First bytes of a file plus its extension, used to pick the handler.</summary>
public sealed record FileSignature(ReadOnlyMemory<byte> Header, string? Extension);

public sealed record PreviewImage(RgbImage Image, int SourceWidth, int SourceHeight, bool IsEmbeddedPreview);

/// <summary>8-bit RGB image, row-major, 3 bytes per pixel, no padding.</summary>
public sealed class RgbImage
{
    public RgbImage(int width, int height, ReadOnlyMemory<byte> pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != checked(width * height * 3))
        {
            throw new ArgumentException($"Expected {width * height * 3} bytes for {width}x{height} RGB, got {pixels.Length}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<byte> Pixels { get; }
}
