// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Core.Imaging;

/// <summary>JPEG previews embedded in a RAW file (extracted by exiftool), with the RAW orientation.</summary>
public interface IEmbeddedPreviewSource
{
    /// <summary>The largest embedded JPEG (JpgFromRaw, PreviewImage, OtherImage), and whether the RAW data can be decoded.</summary>
    Task<EmbeddedPreviews> ExtractLargestAsync(string rawPath, CancellationToken ct);
}

/// <param name="Orientation">EXIF orientation of the RAW (1-8), applied to the preview.</param>
public sealed record EmbeddedPreview(byte[] Jpeg, string Tag, int Orientation);

/// <param name="Largest">The largest embedded JPEG; null when there is none.</param>
/// <param name="FullDecodingSupported">
/// False for Nikon High Efficiency NEF (TicoRAW): LibRaw returns a wrong image without an error, so only the embedded
/// preview may be used (R15).
/// </param>
public sealed record EmbeddedPreviews(EmbeddedPreview? Largest, bool FullDecodingSupported = true);

/// <summary>Full decoding of a RAW file: the fallback when no usable embedded preview exists (spec §4).</summary>
public interface IRawDecoder
{
    Task<RgbImage> DecodeAsync(string rawPath, int maxSize, CancellationToken ct);
}

/// <summary>An image that cannot be decoded (truncated, corrupt or unsupported): reported, never modified.</summary>
public sealed class ImageDecodeException(string message, Exception? inner = null) : IOException(message, inner);
