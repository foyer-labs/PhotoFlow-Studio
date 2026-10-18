// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Imaging;

namespace PhotoFlow.Imaging;

/// <summary>
/// Separable convolution resampling with the same coefficients and 8-bit fixed-point arithmetic as
/// Pillow's <c>Image.resize</c> (libImaging/Resample.c): horizontal pass, then vertical pass, with the
/// filter support scaled by the reduction factor (antialiasing on downsampling). Model preprocessing
/// in the Python references uses Pillow, so matching it keeps the parity tests tight.
/// </summary>
public static class PillowResampler
{
    private const int PrecisionBits = 32 - 8 - 2;

    public static RgbImage Resize(RgbImage image, int width, int height, ResampleMode mode)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (width == image.Width && height == image.Height)
        {
            return image;
        }

        return Resize(image, width, height, mode, new SourceBox(0, 0, image.Width, image.Height));
    }

    /// <summary>
    /// Pillow <c>Image.resize(size, BILINEAR | BICUBIC, box)</c>: resamples the fractional source <paramref name="box"/>
    /// (single precision, as Pillow) to <paramref name="width"/> × <paramref name="height"/>. Filter taps outside the
    /// box but inside the image are used, as in Pillow.
    /// </summary>
    public static RgbImage Resize(RgbImage image, int width, int height, ResampleMode mode, SourceBox box)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var (filter, filterSupport) = mode switch
        {
            ResampleMode.Bilinear => ((Func<double, double>)Triangle, 1.0),
            ResampleMode.Bicubic => (Bicubic, 2.0),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported resample mode."),
        };

        if (box.X0 < 0 || box.Y0 < 0 || box.X1 > image.Width || box.Y1 > image.Height || box.X1 < box.X0 || box.Y1 < box.Y0)
        {
            throw new ArgumentOutOfRangeException(nameof(box), box, "The box must lie inside the image.");
        }

        var source = image.Pixels.Span;
        var needHorizontal = width != image.Width || box.X0 != 0 || box.X1 != image.Width;
        var needVertical = height != image.Height || box.Y0 != 0 || box.Y1 != image.Height;

        byte[] horizontal;
        if (needHorizontal)
        {
            horizontal = new byte[width * image.Height * 3];
            var (bounds, coeffs, kernelSize) = PrecomputeCoefficients(image.Width, box.X0, box.X1, width, filter, filterSupport);
            ResampleHorizontal(source, image.Width, image.Height, horizontal, width, bounds, coeffs, kernelSize);
        }
        else
        {
            horizontal = source.ToArray();
        }

        if (!needVertical)
        {
            return new RgbImage(width, height, horizontal);
        }

        var output = new byte[width * height * 3];
        var (vBounds, vCoeffs, vKernelSize) = PrecomputeCoefficients(image.Height, box.Y0, box.Y1, height, filter, filterSupport);
        ResampleVertical(horizontal, width, output, height, vBounds, vCoeffs, vKernelSize);
        return new RgbImage(width, height, output);
    }

    private static double Triangle(double x)
    {
        if (x < 0.0)
        {
            x = -x;
        }

        return x < 1.0 ? 1.0 - x : 0.0;
    }

    /// <summary>Pillow <c>bicubic_filter</c> (a = −0.5).</summary>
    private static double Bicubic(double x)
    {
        const double a = -0.5;
        if (x < 0.0)
        {
            x = -x;
        }

        if (x < 1.0)
        {
            return ((((a + 2.0) * x) - (a + 3.0)) * x * x) + 1;
        }

        if (x < 2.0)
        {
            return ((((x - 5) * x) + 8) * x - 4) * a;
        }

        return 0.0;
    }

    /// <summary>Port of <c>precompute_coeffs</c> + <c>normalize_coeffs_8bpc</c>.</summary>
    private static (int[] Bounds, int[] Coefficients, int KernelSize) PrecomputeCoefficients(
        int inSize, float in0, float in1, int outSize, Func<double, double> filter, double filterSupport)
    {
        var scale = (double)(in1 - in0) / outSize;
        var filterScale = Math.Max(scale, 1.0);
        var support = filterSupport * filterScale;
        var kernelSize = (int)Math.Ceiling(support) * 2 + 1;

        var bounds = new int[outSize * 2];
        var coefficients = new int[outSize * kernelSize];
        var weights = new double[kernelSize];

        for (var xx = 0; xx < outSize; xx++)
        {
            var center = in0 + (xx + 0.5) * scale;
            var ss = 1.0 / filterScale;

            // C casts truncate toward zero.
            var xmin = (int)(center - support + 0.5);
            if (xmin < 0)
            {
                xmin = 0;
            }

            var xmax = (int)(center + support + 0.5);
            if (xmax > inSize)
            {
                xmax = inSize;
            }

            xmax -= xmin;

            var total = 0.0;
            Array.Clear(weights);
            for (var x = 0; x < xmax; x++)
            {
                var w = filter((x + xmin - center + 0.5) * ss);
                weights[x] = w;
                total += w;
            }

            for (var x = 0; x < xmax; x++)
            {
                var normalized = total != 0.0 ? weights[x] / total : weights[x];
                coefficients[xx * kernelSize + x] = normalized < 0
                    ? (int)(-0.5 + normalized * (1 << PrecisionBits))
                    : (int)(0.5 + normalized * (1 << PrecisionBits));
            }

            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }

        return (bounds, coefficients, kernelSize);
    }

    private static byte Clip8(int value)
    {
        var shifted = value >> PrecisionBits;
        return shifted < 0 ? (byte)0 : shifted > 255 ? (byte)255 : (byte)shifted;
    }

    private static void ResampleHorizontal(
        ReadOnlySpan<byte> input, int inWidth, int rows, Span<byte> output, int outWidth, int[] bounds, int[] coefficients, int kernelSize)
    {
        var inStride = inWidth * 3;
        var outStride = outWidth * 3;
        for (var y = 0; y < rows; y++)
        {
            var inRow = input.Slice(y * inStride, inStride);
            var outRow = output.Slice(y * outStride, outStride);
            for (var xx = 0; xx < outWidth; xx++)
            {
                var xmin = bounds[xx * 2];
                var xmax = bounds[xx * 2 + 1];
                var k = coefficients.AsSpan(xx * kernelSize, kernelSize);
                int r = 1 << (PrecisionBits - 1), g = r, b = r;
                for (var x = 0; x < xmax; x++)
                {
                    var p = (x + xmin) * 3;
                    r += inRow[p] * k[x];
                    g += inRow[p + 1] * k[x];
                    b += inRow[p + 2] * k[x];
                }

                outRow[xx * 3] = Clip8(r);
                outRow[xx * 3 + 1] = Clip8(g);
                outRow[xx * 3 + 2] = Clip8(b);
            }
        }
    }

    private static void ResampleVertical(
        ReadOnlySpan<byte> input, int width, Span<byte> output, int outHeight, int[] bounds, int[] coefficients, int kernelSize)
    {
        var stride = width * 3;
        for (var yy = 0; yy < outHeight; yy++)
        {
            var ymin = bounds[yy * 2];
            var ymax = bounds[yy * 2 + 1];
            var k = coefficients.AsSpan(yy * kernelSize, kernelSize);
            var outRow = output.Slice(yy * stride, stride);
            for (var i = 0; i < stride; i++)
            {
                var sum = 1 << (PrecisionBits - 1);
                for (var y = 0; y < ymax; y++)
                {
                    sum += input[(y + ymin) * stride + i] * k[y];
                }

                outRow[i] = Clip8(sum);
            }
        }
    }
}

