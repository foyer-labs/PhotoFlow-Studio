// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Imaging;

namespace PhotoFlow.Imaging;

/// <summary>Pixel-exact geometric transforms on <see cref="RgbImage"/>.</summary>
public static class ImageTransforms
{
    /// <summary>Applies an EXIF orientation (1-8) so that the image is displayed upright.</summary>
    public static RgbImage ApplyOrientation(RgbImage image, int orientation)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (orientation is <= 1 or > 8)
        {
            return image;
        }

        var w = image.Width;
        var h = image.Height;
        var swap = orientation >= 5;
        var outW = swap ? h : w;
        var outH = swap ? w : h;
        var source = image.Pixels.Span;
        var output = new byte[source.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (dx, dy) = orientation switch
                {
                    2 => (w - 1 - x, y),          // mirror horizontal
                    3 => (w - 1 - x, h - 1 - y),  // rotate 180
                    4 => (x, h - 1 - y),          // mirror vertical
                    5 => (y, x),                  // transpose
                    6 => (h - 1 - y, x),          // rotate 90 CW
                    7 => (h - 1 - y, w - 1 - x),  // transverse
                    _ => (y, w - 1 - x),          // 8: rotate 270 CW
                };
                var s = (y * w + x) * 3;
                var d = (dy * outW + dx) * 3;
                output[d] = source[s];
                output[d + 1] = source[s + 1];
                output[d + 2] = source[s + 2];
            }
        }

        return new RgbImage(outW, outH, output);
    }

    /// <summary>Downscales so that the long side is at most <paramref name="maxSize"/> (never upscales).</summary>
    public static RgbImage FitWithin(RgbImage image, int maxSize)
    {
        ArgumentNullException.ThrowIfNull(image);
        var longSide = Math.Max(image.Width, image.Height);
        if (longSide <= maxSize)
        {
            return image;
        }

        var scale = (double)maxSize / longSide;
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        return PillowResampler.Resize(image, width, height, ResampleMode.Bilinear);
    }
}
