// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Core.Metadata;

/// <summary>Reads the EXIF data of spec §4 for a batch of files.</summary>
public interface IExifReader
{
    Task<IReadOnlyList<ExifInfo>> ReadAsync(IReadOnlyList<string> paths, CancellationToken ct);
}

/// <summary>Nikon High Efficiency NEF (TicoRAW, "High Efficiency" and "High Efficiency*"), from the NEFCompression tag.</summary>
public static class HighEfficiencyRaw
{
    public static bool Matches(string? nefCompression) =>
        nefCompression is { } c && c.Contains("High Efficiency", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The format recorded for an asset: the file type read by exiftool, else the extension.</summary>
public static class AssetFormat
{
    public static string Of(string? exifFileType, string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

        // exiftool names some Nikon Z NEFs "NRW" (e.g. the Z5 II High Efficiency ones): the camera wrote a NEF.
        return exifFileType is null || (exifFileType.Equals("NRW", StringComparison.OrdinalIgnoreCase) && extension == "NEF")
            ? extension
            : exifFileType.ToUpperInvariant();
    }
}

/// <summary>EXIF data used by PhotoFlow. Capture time keeps its sub-seconds (burst order) and records its source.</summary>
public sealed record ExifInfo(
    string Path,
    string? FileType,
    string? Make,
    string? Model,
    string? Lens,
    int? Iso,
    double? ExposureTimeSeconds,
    double? Aperture,
    double? FocalLengthMm,
    double? ExposureCompensationEv,
    DateTimeOffset? CapturedAt,
    DateSource CaptureDateSource,
    int? Orientation,
    string? AfPoint,
    int? Width,
    int? Height,
    string? RawCompression,
    long? EmbeddedJpegLength,
    long? PreviewImageLength,
    BoundingBox? AfArea = null)
{
    /// <summary>Nikon High Efficiency NEF (TicoRAW): not decodable by LibRaw, preview only (R15).</summary>
    public bool IsHighEfficiencyRaw => HighEfficiencyRaw.Matches(RawCompression);

    public ExifSummary ToSummary() =>
        new(Model, Lens, Iso, ExposureTimeSeconds, Aperture, FocalLengthMm, ExposureCompensationEv, AfPoint, AfArea);
}

public static class AfAreas
{
    /// <summary>
    /// AF area from center coordinates and size in pixels of a <paramref name="frameWidth"/> × <paramref name="frameHeight"/>
    /// frame in the stored (sensor) orientation, normalized and oriented like the displayed image. Missing or
    /// out-of-frame values: no area.
    /// </summary>
    public static BoundingBox? FromCenter(double? x, double? y, double? width, double? height, double? frameWidth, double? frameHeight, int? orientation)
    {
        if (x is not { } cx || y is not { } cy || frameWidth is not { } fw || frameHeight is not { } fh
            || fw <= 0 || fh <= 0 || cx <= 0 || cy <= 0 || cx >= fw || cy >= fh)
        {
            return null;
        }

        var halfWidth = Math.Max(width ?? 0, 0) / 2;
        var halfHeight = Math.Max(height ?? 0, 0) / 2;
        var box = new BoundingBox(
            (float)(Math.Max(cx - halfWidth, 0) / fw),
            (float)(Math.Max(cy - halfHeight, 0) / fh),
            (float)(Math.Min(cx + halfWidth, fw) / fw),
            (float)(Math.Min(cy + halfHeight, fh) / fh));
        return box.Orient(orientation);
    }
}
