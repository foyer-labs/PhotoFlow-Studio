// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Detectors;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class DFinePostProcessingTests
{
    private const int Queries = 3;
    private const int Classes = 4;

    [Fact]
    public void Keeps_scores_above_threshold_sorted_and_converts_boxes()
    {
        var logits = new float[Queries * Classes];
        Array.Fill(logits, -10f);
        logits[0 * Classes + 2] = 3f;    // query 0, class 2, score 0.9526
        logits[2 * Classes + 0] = 0.5f;  // query 2, class 0, score 0.6225
        logits[1 * Classes + 3] = -0.1f; // query 1, class 3, score 0.475 (below 0.5)
        float[] boxes =
        [
            0.5f, 0.5f, 0.2f, 0.4f,
            0.1f, 0.1f, 0.1f, 0.1f,
            0.25f, 0.75f, 0.5f, 0.5f,
        ];

        var detections = DFineDetector.PostProcess(
            NamedTensor.Float32("logits", logits, 1, Queries, Classes),
            NamedTensor.Float32("pred_boxes", boxes, 1, Queries, 4),
            0.5f);

        Assert.Equal(2, detections.Count);
        Assert.Equal(2, detections[0].ClassId);
        Assert.Equal(0.9526f, detections[0].Score, 3);
        Assert.Equal(0.4f, detections[0].Box.X1, 5);
        Assert.Equal(0.3f, detections[0].Box.Y1, 5);
        Assert.Equal(0.6f, detections[0].Box.X2, 5);
        Assert.Equal(0.7f, detections[0].Box.Y2, 5);
        Assert.Equal(0, detections[1].ClassId);
        Assert.Equal("person", detections[1].Label);
        Assert.Equal(0.0f, detections[1].Box.X1, 5);
        Assert.Equal(1.0f, detections[1].Box.Y2, 5);
    }

    [Fact]
    public void Same_query_can_yield_several_classes()
    {
        var logits = new float[Queries * Classes];
        Array.Fill(logits, -10f);
        logits[1 * Classes + 0] = 2f;
        logits[1 * Classes + 1] = 1f;
        var boxes = new float[Queries * 4];

        var detections = DFineDetector.PostProcess(
            NamedTensor.Float32("logits", logits, 1, Queries, Classes),
            NamedTensor.Float32("pred_boxes", boxes, 1, Queries, 4),
            0.5f);

        Assert.Equal([0, 1], detections.Select(d => d.ClassId));
    }

    [Fact]
    public void Rejects_unexpected_shapes() =>
        Assert.Throws<ArgumentException>(() => DFineDetector.PostProcess(
            NamedTensor.Float32("logits", new float[12], 1, 3, 4),
            NamedTensor.Float32("pred_boxes", new float[8], 1, 2, 4),
            0.5f));

    [Fact]
    public void Preprocess_produces_nchw_scaled_to_unit_range()
    {
        byte[] pixels = [255, 0, 51, 0, 255, 102];
        var tensor = DFineDetector.Preprocess(new RgbImage(2, 1, pixels));
        Assert.Equal([1, 3, 1, 2], tensor.Shape);
        Assert.Equal([1f, 0f, 0f, 1f, 0.2f, 0.4f], tensor.AsFloat32().ToArray());
    }
}
