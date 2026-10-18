// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Imaging;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class AfAreaTests
{
    [Fact]
    public void Nikon_center_coordinates_become_a_normalized_box()
    {
        // Values of a D5100 live view NEF: wide area AF in the center of a 4928 × 3264 frame.
        var box = AfAreas.FromCenter(2464, 1632, 816, 672, 4928, 3264, orientation: 1);
        Assert.NotNull(box);
        Assert.Equal(2056f / 4928, box.Value.X1, 5);
        Assert.Equal(1296f / 3264, box.Value.Y1, 5);
        Assert.Equal(2872f / 4928, box.Value.X2, 5);
        Assert.Equal(1968f / 3264, box.Value.Y2, 5);
    }

    [Theory]
    [InlineData(null, 10.0)]
    [InlineData(0.0, 10.0)]
    [InlineData(5000.0, 10.0)]
    [InlineData(100.0, null)]
    public void Missing_or_out_of_frame_values_give_no_area(double? x, double? frameHeight)
    {
        Assert.Null(AfAreas.FromCenter(x, 5, 2, 2, 4928, frameHeight, 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Orienting_a_box_follows_the_pixels(int orientation)
    {
        // A 6×4 image with one marked pixel at (4, 1); the box covering exactly that pixel must land on it after orientation.
        const int w = 6, h = 4;
        var pixels = new byte[w * h * 3];
        pixels[((1 * w) + 4) * 3] = 255;
        var oriented = ImageTransforms.ApplyOrientation(new RgbImage(w, h, pixels), orientation);
        var box = new BoundingBox(4f / w, 1f / h, 5f / w, 2f / h).Orient(orientation);

        var x = (int)Math.Round(box.X1 * oriented.Width);
        var y = (int)Math.Round(box.Y1 * oriented.Height);
        Assert.Equal(1, (int)Math.Round(box.Width * oriented.Width));
        Assert.Equal(1, (int)Math.Round(box.Height * oriented.Height));
        Assert.Equal(255, oriented.Pixels.Span[((y * oriented.Width) + x) * 3]);
    }
}
