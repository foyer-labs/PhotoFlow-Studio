// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;

namespace PhotoFlow.Faces.Recognition;

/// <summary>
/// Face alignment for the recognition models (spec §7): the 5 keypoints (eyes, nose, mouth corners) are mapped onto
/// the ArcFace 112×112 template with a similarity transform estimated as scikit-image does (Umeyama, with scale), and
/// the crop is taken with an affine bilinear warp (OpenCV <c>warpAffine</c> semantics, black border).
/// </summary>
public static class FaceAlignment
{
    public const int CropSize = 112;

    /// <summary>insightface <c>arcface_dst</c> (also the template of OpenCV's FaceRecognizerSF).</summary>
    public static readonly IReadOnlyList<Point2> ArcFaceTemplate =
    [
        new(38.2946f, 51.6963f), new(73.5318f, 51.5014f), new(56.0252f, 71.7366f), new(41.5493f, 92.3655f), new(70.7299f, 92.2041f),
    ];

    /// <summary>2×3 matrix (row major) mapping image points onto the template: insightface <c>estimate_norm</c>.</summary>
    public static double[] EstimateNorm(IReadOnlyList<Point2> keypoints)
    {
        ArgumentNullException.ThrowIfNull(keypoints);
        if (keypoints.Count != 5)
        {
            throw new ArgumentException("Five keypoints expected.", nameof(keypoints));
        }

        return Umeyama(keypoints, ArcFaceTemplate);
    }

    /// <summary>
    /// Least-squares similarity transform (rotation, uniform scale, translation) from <paramref name="source"/> to
    /// <paramref name="destination"/>: port of scikit-image <c>_umeyama(src, dst, estimate_scale=True)</c>, in double.
    /// </summary>
    public static double[] Umeyama(IReadOnlyList<Point2> source, IReadOnlyList<Point2> destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var n = source.Count;
        double smx = 0, smy = 0, dmx = 0, dmy = 0;
        for (var i = 0; i < n; i++)
        {
            smx += source[i].X;
            smy += source[i].Y;
            dmx += destination[i].X;
            dmy += destination[i].Y;
        }

        smx /= n;
        smy /= n;
        dmx /= n;
        dmy /= n;

        // A = dst_demeanᵀ · src_demean / n (2×2), source variance.
        double a00 = 0, a01 = 0, a10 = 0, a11 = 0, variance = 0;
        for (var i = 0; i < n; i++)
        {
            var sx = source[i].X - smx;
            var sy = source[i].Y - smy;
            var dx = destination[i].X - dmx;
            var dy = destination[i].Y - dmy;
            a00 += dx * sx;
            a01 += dx * sy;
            a10 += dy * sx;
            a11 += dy * sy;
            variance += (sx * sx) + (sy * sy);
        }

        a00 /= n;
        a01 /= n;
        a10 /= n;
        a11 /= n;
        variance /= n;

        // SVD of the 2×2 matrix: A = U S Vᵀ, with d = sign(det A) correcting reflections.
        var (u, s, vt) = Svd2(a00, a01, a10, a11);
        var det = (a00 * a11) - (a01 * a10);
        var d1 = det < 0 ? -1.0 : 1.0;

        // T = U · diag(1, d) · Vᵀ
        double r00 = (u[0] * vt[0]) + (d1 * u[1] * vt[2]);
        double r01 = (u[0] * vt[1]) + (d1 * u[1] * vt[3]);
        double r10 = (u[2] * vt[0]) + (d1 * u[3] * vt[2]);
        double r11 = (u[2] * vt[1]) + (d1 * u[3] * vt[3]);
        var scale = variance > 0 ? (s[0] + (d1 * s[1])) / variance : 1;

        var tx = dmx - (scale * ((r00 * smx) + (r01 * smy)));
        var ty = dmy - (scale * ((r10 * smx) + (r11 * smy)));
        return [scale * r00, scale * r01, tx, scale * r10, scale * r11, ty];
    }

    /// <summary>Singular value decomposition of a 2×2 matrix (U and Vᵀ row major, singular values descending).</summary>
    private static (double[] U, double[] S, double[] Vt) Svd2(double a, double b, double c, double d)
    {
        // AᵀA eigen-decomposition gives V and the singular values; U = A V / σ.
        var e = (a * a) + (c * c);
        var f = (a * b) + (c * d);
        var g = (b * b) + (d * d);
        var theta = 0.5 * Math.Atan2(2 * f, e - g);
        var (cos, sin) = (Math.Cos(theta), Math.Sin(theta));
        var s1 = Math.Sqrt(Math.Max(0, (e * cos * cos) + (2 * f * cos * sin) + (g * sin * sin)));
        var s2 = Math.Sqrt(Math.Max(0, (e * sin * sin) - (2 * f * cos * sin) + (g * cos * cos)));
        double[] v = [cos, -sin, sin, cos]; // columns v1 = (cos, sin), v2 = (−sin, cos)
        if (s2 > s1)
        {
            (s1, s2) = (s2, s1);
            v = [-sin, cos, cos, sin];
        }

        // u_i = A v_i / s_i
        var u1x = s1 > 0 ? ((a * v[0]) + (b * v[2])) / s1 : 1;
        var u1y = s1 > 0 ? ((c * v[0]) + (d * v[2])) / s1 : 0;
        double u2x, u2y;
        if (s2 > 1e-12)
        {
            u2x = ((a * v[1]) + (b * v[3])) / s2;
            u2y = ((c * v[1]) + (d * v[3])) / s2;
        }
        else
        {
            (u2x, u2y) = (-u1y, u1x);
        }

        return ([u1x, u2x, u1y, u2y], [s1, s2], [v[0], v[2], v[1], v[3]]);
    }

    /// <summary>The aligned 112×112 crop of a face.</summary>
    public static RgbImage AlignedCrop(RgbImage image, IReadOnlyList<Point2> keypoints) =>
        AffineWarp.Bilinear(image, EstimateNorm(keypoints), CropSize, CropSize);
}

/// <summary>
/// Affine warp with bilinear interpolation and a black constant border, as OpenCV 5 <c>cv::warpAffine</c>
/// (INTER_LINEAR, 8-bit, 3 channels): the forward matrix is inverted in double, source coordinates are computed in
/// single precision, and the four neighbours outside the image count as black.
/// </summary>
public static class AffineWarp
{
    public static RgbImage Bilinear(RgbImage source, IReadOnlyList<double> forward, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(forward);
        var m = Invert(forward);
        float m0 = (float)m[0], m1 = (float)m[1], m2 = (float)m[2], m3 = (float)m[3], m4 = (float)m[4], m5 = (float)m[5];
        var src = source.Pixels.Span;
        var (sw, sh) = (source.Width, source.Height);
        var output = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var rowX = (m1 * y) + m2;
            var rowY = (m4 * y) + m5;
            for (var x = 0; x < width; x++)
            {
                var sx = (m0 * x) + rowX;
                var sy = (m3 * x) + rowY;
                var ix = (int)MathF.Floor(sx);
                var iy = (int)MathF.Floor(sy);
                var alpha = sx - ix;
                var beta = sy - iy;
                var o = ((y * width) + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var p00 = Pixel(src, sw, sh, ix, iy, c);
                    var p01 = Pixel(src, sw, sh, ix + 1, iy, c);
                    var p10 = Pixel(src, sw, sh, ix, iy + 1, c);
                    var p11 = Pixel(src, sw, sh, ix + 1, iy + 1, c);
                    var top = p00 + (alpha * (p01 - p00));
                    var bottom = p10 + (alpha * (p11 - p10));
                    var value = top + (beta * (bottom - top));
                    output[o + c] = (byte)Math.Clamp((int)MathF.Round(value, MidpointRounding.ToEven), 0, 255);
                }
            }
        }

        return new RgbImage(width, height, output);
    }

    /// <summary>OpenCV's inversion of a 2×3 affine matrix (double).</summary>
    public static double[] Invert(IReadOnlyList<double> m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var d = (m[0] * m[4]) - (m[1] * m[3]);
        d = d != 0 ? 1.0 / d : 0;
        var a11 = m[4] * d;
        var a22 = m[0] * d;
        var a12 = -m[1] * d;
        var a21 = -m[3] * d;
        var b1 = (-a11 * m[2]) - (a12 * m[5]);
        var b2 = (-a21 * m[2]) - (a22 * m[5]);
        return [a11, a12, b1, a21, a22, b2];
    }

    private static float Pixel(ReadOnlySpan<byte> src, int width, int height, int x, int y, int c) =>
        (uint)x < (uint)width && (uint)y < (uint)height ? src[(((y * width) + x) * 3) + c] : 0;
}
