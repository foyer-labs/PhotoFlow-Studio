// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using PhotoFlow.Analysis.Culling;
using PhotoFlow.Analysis.Signals;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Imaging;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>The C# signals reproduce OpenCV 5.0 (decision F1-4) and scikit-image's blur effect. Expected values from tools/lab/opencv_refs.py.</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class ImageSignalsTests
{
    private const string Gray =
        "1,15,29,43,57,71,85,22,36,50,64,78,92,35,53,70,87,104,122,139,80,97,85,103,120,137,70,90,111,131,152,143,164,108,129,120,141,161,182,104,128,152,147,170,194,189,136,160,155,179,203,198,138,166,193,191,218,216,167,165,192,40,67,65,92,23,53,54,85,86,117,42,72,73,75,105,107,137,57,91,95,100,134,139,67,100,105,110,144,148,153,91,128,136,144,152,113,121,129,137,145,182,190,121,126,166,177,189,200,135,146,157,168,180,191,231,166";

    private const string Blur =
        "26,34,50,65,81,96,93,70,63,71,83,99,107,44,53,70,87,103,116,113,92,84,91,103,120,128,80,90,109,126,141,152,147,127,121,127,141,160,168,116,128,147,159,174,182,169,153,144,134,140,156,163,115,126,144,157,171,171,151,141,130,103,99,114,120,76,85,101,115,131,126,102,99,100,92,98,112,118,74,83,97,109,122,114,94,95,105,117,136,145,145,110,121,136,146,147,131,120,127,137,150,171,174,165,128,140,157,166,161,139,134,143,153,166,187,188,177";

    private static RgbImage Synthetic(int width = 13, int height = 9)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 3;
                pixels[i] = (byte)((x * 37 + y * 11) % 256);
                pixels[i + 1] = (byte)((x * 5 + y * 53) % 256);
                pixels[i + 2] = (byte)((x * y * 29 + 7) % 256);
            }
        }

        return new RgbImage(width, height, pixels);
    }

    [Fact]
    public void Gray_and_blur_are_bit_exact_with_opencv()
    {
        var gray = ImageSignals.ToGray(Synthetic());
        Assert.Equal(Gray.Split(',').Select(byte.Parse), gray.Pixels);
        Assert.Equal(Blur.Split(',').Select(byte.Parse), ImageSignals.GaussianBlur3(gray).Pixels);
    }

    [Fact]
    public void Laplacian_variance_matches_opencv_on_the_image_and_on_a_zone()
    {
        var blur = ImageSignals.GaussianBlur3(ImageSignals.ToGray(Synthetic()));
        Assert.Equal(1166.0132953466286, ImageSignals.LaplacianVariance(blur, PixelRect.Full(13, 9)), 9);
        Assert.Equal(1009.0481632653062, ImageSignals.LaplacianVariance(blur, new PixelRect(2, 1, 7, 5)), 9);
    }

    [Fact]
    public void Blur_effect_matches_scikit_image()
    {
        var gray = ImageSignals.ToGray(Synthetic());
        Assert.Equal(0.19200842604173535, ImageSignals.BlurEffect(gray, PixelRect.Full(13, 9)), 12);
        Assert.Equal(0.15807252856433168, ImageSignals.BlurEffect(gray, new PixelRect(2, 1, 7, 5)), 12);

        var big = ImageSignals.ToGray(Synthetic(64, 48));
        Assert.Equal(0.18216183906977584, ImageSignals.BlurEffect(big, PixelRect.Full(64, 48)), 12);
        Assert.Equal(0.1699626943071321, ImageSignals.BlurEffect(big, new PixelRect(5, 7, 30, 20)), 12);
    }

    [Fact]
    public void A_blurred_copy_is_less_sharp_than_the_original()
    {
        var image = Synthetic();
        var gray = ImageSignals.ToGray(image);
        var softer = ImageSignals.GaussianBlur3(ImageSignals.GaussianBlur3(gray));
        Assert.True(ImageSignals.Sharpness(softer, PixelRect.Full(13, 9)) < ImageSignals.Sharpness(gray, PixelRect.Full(13, 9)));
        Assert.True(ImageSignals.BlurEffect(softer, PixelRect.Full(13, 9)) > ImageSignals.BlurEffect(gray, PixelRect.Full(13, 9)));
    }

    [Fact]
    public void Exposure_counts_clipped_highlights_and_crushed_shadows()
    {
        byte[] pixels = [255, 255, 255, 0, 0, 0, 128, 128, 128, 251, 10, 10];
        var image = new RgbImage(2, 2, pixels);
        var stats = ImageSignals.Exposure(image, ImageSignals.ToGray(image), PixelRect.Full(2, 2));
        Assert.Equal(0.5, stats.HighlightClip);
        Assert.Equal(0.25, stats.ShadowClip);
    }
}

[Trait(TestCategory.Name, TestCategory.Parity)]
public sealed class ImageSignalsParityTests
{
    [Fact]
    public void Sharpness_matches_opencv_on_real_photos()
    {
        var folder = TestData.RequireDirectory("parity", "opencv");
        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "index.json")))!.AsArray();
        Assert.NotEmpty(index);
        var analyzer = new CullingAnalyzer(new SkiaImageOperations(), objects: null, faces: null);
        foreach (var row in index)
        {
            var width = row!["width"]!.GetValue<int>();
            var height = row["height"]!.GetValue<int>();
            var image = new RgbImage(width, height, File.ReadAllBytes(Path.Combine(folder, row["image"]!.GetValue<string>() + ".rgb")));
            var zone = row["zone"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
            var gray = ImageSignals.ToGray(image);

            Assert.Equal(row["variance_full"]!.GetValue<double>(), ImageSignals.Sharpness(gray, PixelRect.Full(width, height)), 6);
            Assert.Equal(row["variance_zone"]!.GetValue<double>(), ImageSignals.Sharpness(gray, new PixelRect(zone[0], zone[1], zone[2], zone[3])), 6);
            Assert.Equal(row["blur_full"]!.GetValue<double>(), ImageSignals.BlurEffect(gray, PixelRect.Full(width, height)), 9);
            Assert.Equal(row["blur_zone"]!.GetValue<double>(), ImageSignals.BlurEffect(gray, new PixelRect(zone[0], zone[1], zone[2], zone[3])), 9);
            Assert.Equal(row["blur_multiscale_full"]!.GetValue<double>(), analyzer.MultiScaleBlur(gray, PixelRect.Full(width, height)), 9);
            Assert.Equal(row["blur_multiscale_zone"]!.GetValue<double>(), analyzer.MultiScaleBlur(gray, new PixelRect(zone[0], zone[1], zone[2], zone[3])), 9);
        }
    }
}
