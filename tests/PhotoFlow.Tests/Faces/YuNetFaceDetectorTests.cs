// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Inference;
using PhotoFlow.Faces.Detection;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Faces;

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class YuNetFaceDetectorTests
{
    [Fact]
    public void Decoding_follows_opencv_and_keeps_the_best_of_overlapping_faces()
    {
        var outputs = new List<NamedTensor>();
        foreach (var width in new[] { 1, 1, 4, 10 })
        {
            foreach (var stride in new[] { 8, 16, 32 })
            {
                var count = (640 / stride) * (640 / stride);
                outputs.Add(NamedTensor.Float32("o", new float[count * width], 1, count, width));
            }
        }

        // Stride 16, cell (row 3, col 5): cls 0.81 × obj 1 → score 0.9; a neighbouring weaker duplicate.
        Set(outputs[1], 1, (3 * 40) + 5, 0.81f);
        Set(outputs[4], 1, (3 * 40) + 5, 1f);
        Set(outputs[7], 4, (3 * 40) + 5, 0.5f, 0.5f, MathF.Log(4), MathF.Log(4));
        Set(outputs[1], 1, (3 * 40) + 6, 0.49f);
        Set(outputs[4], 1, (3 * 40) + 6, 1f);
        Set(outputs[7], 4, (3 * 40) + 6, -0.5f, 0.5f, MathF.Log(4), MathF.Log(4));

        var face = Assert.Single(YuNetFaceDetector.Decode(outputs, 0.6f, 0.3f));
        Assert.Equal(0.9f, face.Score, 5);
        Assert.Equal(5.5f * 16 - 32, face.X, 4);
        Assert.Equal(3.5f * 16 - 32, face.Y, 4);
        Assert.Equal(64f, face.Width, 4);
        Assert.Equal(new PhotoFlow.Core.Catalog.Point2(80, 48), face.Keypoints[0]);
    }

    private static void Set(NamedTensor tensor, int width, int index, params float[] values)
    {
        var data = System.Runtime.InteropServices.MemoryMarshal.AsMemory(tensor.AsFloat32()).Span;
        values.CopyTo(data[(index * width)..]);
    }
}
