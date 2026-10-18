// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;

namespace PhotoFlow.Core.Analysis;

/// <summary>Per-photo culling analyzer (exposure, sharpness, subject, faces/eyes, embedding).</summary>
public interface IPhotoAnalyzer
{
    string Id { get; }

    Task<AnalyzerResult> AnalyzeAsync(PhotoContext photo, SceneProfile profile, CancellationToken ct);
}

/// <summary>Object detection, implemented by a model adapter (e.g. D-FINE).</summary>
public interface IObjectDetector
{
    string ModelVersion { get; }

    Task<IReadOnlyList<Detection>> DetectAsync(RgbImage image, CancellationToken ct);
}

public sealed record PhotoContext(long? AssetId, string Path, PreviewImage Preview, ExifSummary? Exif);

/// <summary>Every automatic result records the model version that produced it (CLAUDE.md §4).</summary>
public sealed record AnalyzerResult(string AnalyzerId, string ModelVersion, IReadOnlyDictionary<string, double> Scores);

/// <summary>Box in normalized coordinates (0..1) relative to the analyzed image.</summary>
public readonly record struct BoundingBox(float X1, float Y1, float X2, float Y2)
{
    public float Width => X2 - X1;

    public float Height => Y2 - Y1;

    public float Area => Math.Max(0, Width) * Math.Max(0, Height);

    /// <summary>
    /// Maps a box from the stored (sensor) frame to the displayed frame of EXIF <paramref name="orientation"/> (1-8),
    /// the same transform the image handlers apply to the pixels.
    /// </summary>
    public BoundingBox Orient(int? orientation)
    {
        (float X, float Y) Map(float x, float y) => orientation switch
        {
            2 => (1 - x, y),
            3 => (1 - x, 1 - y),
            4 => (x, 1 - y),
            5 => (y, x),
            6 => (1 - y, x),
            7 => (1 - y, 1 - x),
            8 => (y, 1 - x),
            _ => (x, y),
        };

        var a = Map(X1, Y1);
        var b = Map(X2, Y2);
        return new BoundingBox(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    public float IntersectionOverUnion(BoundingBox other)
    {
        var ix = Math.Max(0, Math.Min(X2, other.X2) - Math.Max(X1, other.X1));
        var iy = Math.Max(0, Math.Min(Y2, other.Y2) - Math.Max(Y1, other.Y1));
        var intersection = ix * iy;
        var union = Area + other.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}

public sealed record Detection(int ClassId, string Label, float Score, BoundingBox Box);
