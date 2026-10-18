// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;

namespace PhotoFlow.Analysis.Detectors;

/// <summary>
/// Adapter for D-FINE exported with the Hugging Face I/O contract
/// (<c>pixel_values</c> → <c>logits</c> [1,Q,C], <c>pred_boxes</c> [1,Q,4] cxcywh normalized).
/// Preprocessing and post-processing mirror <c>RTDetrImageProcessor</c> (resize 640×640 bilinear without
/// keeping the aspect ratio, rescale 1/255, no normalization; sigmoid scores, top-k over queries × classes).
/// </summary>
public sealed class DFineDetector : IObjectDetector
{
    public const string AdapterId = "dfine-hf-v1";
    public const int InputSize = 640;
    public const string InputName = "pixel_values";
    public const string LogitsName = "logits";
    public const string BoxesName = "pred_boxes";
    public const float DefaultScoreThreshold = 0.5f;

    private readonly IInferenceSession _session;
    private readonly IImageOperations _imageOperations;
    private readonly float _scoreThreshold;

    public DFineDetector(IInferenceSession session, IImageOperations imageOperations, float scoreThreshold = DefaultScoreThreshold)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(imageOperations);
        _session = session;
        _imageOperations = imageOperations;
        _scoreThreshold = scoreThreshold;
    }

    public string ModelVersion => $"{_session.Model.Id}@{_session.Model.Version}";

    public async Task<IReadOnlyList<Detection>> DetectAsync(RgbImage image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        var resized = _imageOperations.Resize(image, InputSize, InputSize, ResampleMode.Bilinear);
        var outputs = await RunRawAsync(Preprocess(resized), ct).ConfigureAwait(false);
        return PostProcess(outputs.Logits, outputs.Boxes, _scoreThreshold);
    }

    /// <summary>Runs the model on an already prepared input tensor (used by the tensor-level parity test).</summary>
    public async Task<(NamedTensor Logits, NamedTensor Boxes)> RunRawAsync(NamedTensor pixelValues, CancellationToken ct)
    {
        var outputs = await _session.RunAsync([pixelValues], ct).ConfigureAwait(false);
        var logits = outputs.FirstOrDefault(t => t.Name == LogitsName)
            ?? throw new InvalidOperationException($"Model output '{LogitsName}' missing.");
        var boxes = outputs.FirstOrDefault(t => t.Name == BoxesName)
            ?? throw new InvalidOperationException($"Model output '{BoxesName}' missing.");
        return (logits, boxes);
    }

    /// <summary>RGB HWC bytes → float32 NCHW scaled to 0..1 (same rounding as numpy: float64 product, then float32).</summary>
    public static NamedTensor Preprocess(RgbImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var planeSize = image.Width * image.Height;
        var data = new float[planeSize * 3];
        var pixels = image.Pixels.Span;
        const double scale = 1.0 / 255.0;
        for (var i = 0; i < planeSize; i++)
        {
            data[i] = (float)(pixels[i * 3] * scale);
            data[planeSize + i] = (float)(pixels[i * 3 + 1] * scale);
            data[2 * planeSize + i] = (float)(pixels[i * 3 + 2] * scale);
        }

        return NamedTensor.Float32(InputName, data, 1, 3, image.Height, image.Width);
    }

    /// <summary>Port of <c>RTDetrImageProcessor.post_process_object_detection(use_focal_loss=True)</c>, boxes kept normalized.</summary>
    public static IReadOnlyList<Detection> PostProcess(NamedTensor logits, NamedTensor boxes, float scoreThreshold)
    {
        ArgumentNullException.ThrowIfNull(logits);
        ArgumentNullException.ThrowIfNull(boxes);
        if (logits.Shape.Count != 3 || boxes.Shape.Count != 3 || logits.Shape[0] != 1 || boxes.Shape[0] != 1
            || boxes.Shape[2] != 4 || logits.Shape[1] != boxes.Shape[1])
        {
            throw new ArgumentException(
                $"Unexpected D-FINE output shapes: logits [{string.Join(",", logits.Shape)}], boxes [{string.Join(",", boxes.Shape)}].");
        }

        var queries = logits.Shape[1];
        var classes = logits.Shape[2];
        var logitValues = logits.AsFloat32().Span;
        var boxValues = boxes.AsFloat32().Span;

        // Top-k (k = number of queries) over the flattened queries × classes scores.
        var flat = new (float Score, int Index)[queries * classes];
        for (var i = 0; i < flat.Length; i++)
        {
            flat[i] = (Sigmoid(logitValues[i]), i);
        }

        Array.Sort(flat, static (a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Index.CompareTo(b.Index));

        var detections = new List<Detection>();
        for (var k = 0; k < queries && k < flat.Length; k++)
        {
            var (score, index) = flat[k];
            if (score <= scoreThreshold)
            {
                break;
            }

            var classId = index % classes;
            var query = index / classes;
            var cx = boxValues[query * 4];
            var cy = boxValues[query * 4 + 1];
            var w = boxValues[query * 4 + 2];
            var h = boxValues[query * 4 + 3];
            var box = new BoundingBox(cx - 0.5f * w, cy - 0.5f * h, cx + 0.5f * w, cy + 0.5f * h);
            detections.Add(new Detection(classId, CocoClasses.NameOf(classId), score, box));
        }

        return detections;
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));
}
