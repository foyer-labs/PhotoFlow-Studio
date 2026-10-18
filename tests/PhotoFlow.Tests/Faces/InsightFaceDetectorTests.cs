// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Inference;
using PhotoFlow.Faces.Detection;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Faces;

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class InsightFaceDetectorTests
{
    [Fact]
    public void Decoding_maps_anchors_to_pixels_and_suppresses_overlaps()
    {
        var outputs = EmptyOutputs();
        // Stride 8, location (x 10, y 5) → center (80, 40); both anchors of the location fire, the second weaker.
        var anchor = ((5 * 80) + 10) * 2;
        SetFace(outputs, level: 0, anchor, score: 0.9f, distance: 2f);
        SetFace(outputs, level: 0, anchor + 1, score: 0.7f, distance: 2.1f);
        // A separate weak face below the threshold.
        SetFace(outputs, level: 1, 10, score: 0.3f, distance: 1f);

        var faces = InsightFaceDetector.Decode(outputs, scale: 0.5, scoreThreshold: 0.5f, nmsThreshold: 0.4f);

        var face = Assert.Single(faces);
        Assert.Equal(0.9f, face.Score);
        Assert.Equal(new PixelBox((80 - 16) / 0.5f, (40 - 16) / 0.5f, (80 + 16) / 0.5f, (40 + 16) / 0.5f), face.Box);
        Assert.Equal(new Point2(160 + 32, 80 + 32), face.Keypoints[0]);
    }

    [Fact]
    public void Eye_aspect_ratio_is_lid_distance_over_width()
    {
        // Corners at x = 0 and 10, lids ±1 → mean vertical 2, ratio 0.2.
        var eye = new EyeContour([
            new(0, 0), new(2.5f, 1), new(5, 1), new(7.5f, 1),
            new(10, 0), new(7.5f, -1), new(5, -1), new(2.5f, -1),
        ]);
        Assert.Equal(0.2, eye.AspectRatio, 6);
        Assert.Equal(10, eye.Width, 6);
    }

    private static List<NamedTensor> EmptyOutputs()
    {
        var outputs = new List<NamedTensor>();
        foreach (var width in new[] { 1, 4, 10 })
        {
            foreach (var stride in new[] { 8, 16, 32 })
            {
                var count = (640 / stride) * (640 / stride) * 2;
                outputs.Add(NamedTensor.Float32("o", new float[count * width], count, width));
            }
        }

        return outputs;
    }

    private static void SetFace(List<NamedTensor> outputs, int level, int anchor, float score, float distance)
    {
        Write(outputs[level], anchor, 1, _ => score);
        Write(outputs[level + 3], anchor, 4, _ => distance);
        Write(outputs[level + 6], anchor, 10, _ => distance);
    }

    private static void Write(NamedTensor tensor, int anchor, int width, Func<int, float> value)
    {
        var data = System.Runtime.InteropServices.MemoryMarshal.AsMemory(tensor.AsFloat32()).Span;
        for (var i = 0; i < width; i++)
        {
            data[(anchor * width) + i] = value(i);
        }
    }
}
