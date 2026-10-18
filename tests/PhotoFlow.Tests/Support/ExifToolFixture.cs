// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Metadata.ExifTool;
using SkiaSharp;

namespace PhotoFlow.Tests.Support;

/// <summary>The exiftool provisioned next to the test assembly by build/ExifTool.targets.</summary>
public static class ExifToolFixture
{
    public static ExifToolSession Start()
    {
        var options = ExifToolOptions.Bundled(AppContext.BaseDirectory);
        if (!File.Exists(options.ExecutablePath))
        {
            if (Environment.GetEnvironmentVariable("CI") is not null)
            {
                throw new FileNotFoundException("exiftool was not provisioned by the build.", options.ExecutablePath);
            }

            Assert.Skip("exiftool not provisioned (build with network access once).");
        }

        return new ExifToolSession(options);
    }

    /// <summary>Reads tags back with an independent exiftool call (no stay_open), as a user would.</summary>
    public static async Task<string> ReadTagAsync(ExifToolSession session, string path, string tag) =>
        (await session.ExecuteAsync(["-s3", "-sep", "|", "-" + tag, path], TestContext.Current.CancellationToken)).Output.Trim();
}

/// <summary>Synthetic JPEG images (no personal photos, no license issues).</summary>
public static class SyntheticImages
{
    /// <summary>A "scene" of coloured rectangles that depends on the seed, drawn at any size in proportion (same picture, different sizes).</summary>
    public static string WriteScene(string path, int width, int height, int seed)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            var random = new Random(seed);
            canvas.Clear(new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            using var paint = new SKPaint();
            for (var i = 0; i < 12; i++)
            {
                paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                var (x, y) = ((float)random.NextDouble() * width, (float)random.NextDouble() * height);
                canvas.DrawRect(x, y, (float)random.NextDouble() * width / 2, (float)random.NextDouble() * height / 2, paint);
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    public static string WriteJpeg(string path, int width = 96, int height = 64, int seed = 1)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var random = new Random(seed);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                pixels[i] = (byte)(x * 255 / width);
                pixels[i + 1] = (byte)(y * 255 / height);
                pixels[i + 2] = (byte)random.Next(256);
                pixels[i + 3] = 255;
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);

        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }
}
