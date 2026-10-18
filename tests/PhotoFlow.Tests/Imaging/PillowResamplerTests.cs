// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Imaging;
using PhotoFlow.Imaging;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Imaging;

/// <summary>
/// The resampler must be bit-exact with Pillow's <c>Image.resize(BILINEAR)</c>, used by the Python references.
/// Expected values generated with Pillow 12.3.0 from a 7×5 synthetic gradient
/// (R = (x·37 + y·11) mod 256, G = (x·5 + y·53) mod 256, B = (x·y·29 + 7) mod 256).
/// </summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class PillowResamplerTests
{
    private static readonly RgbImage Source = CreateSource();

    [Theory]
    [InlineData(3, 4, "35,17,13,114,28,29,193,39,44,48,79,42,127,90,124,206,101,134,60,141,72,139,152,120,218,163,106,73,203,100,152,214,141,163,225,136")]
    [InlineData(4, 5, "19,3,7,77,10,7,145,20,7,203,27,7,30,56,22,88,63,67,156,73,121,214,80,166,41,109,37,99,116,128,167,126,183,225,133,81,52,162,53,110,169,116,178,179,92,236,186,93,63,215,68,121,222,177,189,232,155,113,239,142")]
    [InlineData(11, 9, "0,0,7,17,2,7,40,5,7,64,9,7,87,12,7,111,15,7,135,18,7,158,21,7,182,25,7,205,28,7,222,30,7,4,18,7,21,20,11,44,23,18,68,27,24,91,30,30,115,33,36,139,36,42,162,39,48,186,43,54,209,46,61,226,48,65,10,47,7,27,49,19,50,52,35,74,56,51,97,59,68,121,62,84,145,65,100,168,68,117,192,72,133,215,75,150,232,77,162,16,77,7,33,79,26,56,82,53,80,86,79,103,89,106,127,92,133,151,95,159,174,98,155,198,102,109,221,105,126,238,107,145,22,106,7,39,108,33,62,111,70,86,115,107,109,118,144,133,121,181,157,124,218,180,127,185,204,131,59,227,134,73,244,136,99,28,135,7,45,137,41,68,140,88,92,144,135,115,147,131,139,150,87,163,153,134,186,156,151,210,160,125,233,163,85,250,165,53,34,165,7,51,167,48,74,170,106,98,174,163,121,177,128,145,180,22,169,183,79,192,186,129,216,190,168,224,193,99,228,195,36,40,194,7,57,196,56,80,199,123,104,203,190,127,206,165,151,209,70,175,212,138,198,215,159,222,219,118,153,222,123,92,224,133,44,212,7,61,214,60,84,217,134,108,221,207,131,224,188,155,227,99,179,230,173,202,233,177,226,237,88,110,240,138,10,242,191")]
    public void Matches_pillow_bilinear_exactly(int width, int height, string expected)
    {
        var result = PillowResampler.Resize(Source, width, height, ResampleMode.Bilinear);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal(expected.Split(',').Select(byte.Parse), result.Pixels.ToArray());
    }

    [Theory]
    [InlineData(4, 3, 0.3f, 0.7f, 6.2f, 4.1f, "28,41,18,82,48,48,137,55,79,192,63,109,40,101,36,94,108,114,149,115,188,204,123,105,54,165,54,108,172,137,163,179,79,212,187,132")]
    [InlineData(9, 8, 1.25f, 0.5f, 4.75f, 3.9f, "37,16,13,51,18,15,66,20,18,80,22,20,95,24,23,109,25,25,123,27,27,138,29,30,152,31,32,42,39,24,56,41,32,71,43,39,85,45,46,100,47,54,114,48,61,128,50,68,143,52,75,157,54,82,47,61,36,61,63,48,76,65,60,90,67,72,105,69,85,119,70,96,133,72,108,148,74,120,162,76,131,51,84,48,65,86,65,80,88,81,94,90,98,109,92,115,123,93,132,137,95,148,152,97,165,166,99,175,56,106,60,70,108,81,85,110,103,99,112,124,114,114,146,128,115,168,142,117,189,157,119,211,171,121,219,61,129,71,75,131,97,90,133,124,104,135,140,119,137,133,133,138,126,147,140,143,162,142,169,176,144,186,65,151,83,79,153,114,94,155,145,108,157,154,123,159,110,137,160,65,151,162,74,166,164,106,180,166,133,70,174,94,84,176,130,99,178,166,113,180,173,128,182,111,142,183,47,156,185,54,171,187,90,185,189,123")]
    [InlineData(5, 5, 0f, 0f, 7f, 5f, "12,2,7,58,8,7,111,15,7,164,22,7,210,28,7,23,55,17,69,61,52,122,68,94,175,75,136,221,81,171,34,108,26,80,114,97,133,121,181,186,128,151,232,134,80,45,161,36,91,167,142,144,174,59,197,181,138,243,187,73,56,214,46,102,220,187,155,227,146,208,234,153,83,240,152")]
    [InlineData(3, 2, 2f, 1f, 5f, 3f, "85,63,65,122,68,94,159,73,123,96,116,123,133,121,181,170,126,239")]
    public void Matches_pillow_bilinear_with_a_box_exactly(int width, int height, float x0, float y0, float x1, float y1, string expected)
    {
        var result = PillowResampler.Resize(Source, width, height, ResampleMode.Bilinear, new SourceBox(x0, y0, x1, y1));
        Assert.Equal(expected.Split(',').Select(byte.Parse), result.Pixels.ToArray());
    }

    [Fact]
    public void Same_size_returns_the_input()
    {
        Assert.Same(Source, PillowResampler.Resize(Source, Source.Width, Source.Height, ResampleMode.Bilinear));
    }

    [Fact]
    public void Constant_image_stays_constant()
    {
        var gray = new RgbImage(40, 30, Enumerable.Repeat((byte)128, 40 * 30 * 3).ToArray());
        var result = PillowResampler.Resize(gray, 13, 7, ResampleMode.Bilinear);
        Assert.All(result.Pixels.ToArray(), v => Assert.Equal(128, v));
    }

    private static RgbImage CreateSource()
    {
        const int width = 7, height = 5;
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
}
