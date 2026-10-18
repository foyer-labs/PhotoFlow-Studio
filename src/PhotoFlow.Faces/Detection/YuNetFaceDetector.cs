// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;

namespace PhotoFlow.Faces.Detection;

/// <summary>
/// Adapter for YuNet 2023mar (OpenCV Zoo, MIT): the permissive alternative to buffalo_l. Five keypoints only, so the
/// eyes are always unknown (decision A5). The model input is fixed at 640×640 (note N4): the image is reduced to fit
/// (antialiased) and placed top-left on a black canvas, BGR, values 0..255. Decoding and NMS are a port of OpenCV 5.0
/// <c>FaceDetectorYN</c> (score = √(cls·obj), boxes from center offsets and log sizes, NMS on integer boxes).
/// </summary>
public sealed class YuNetFaceDetector : IFaceDetector
{
    public const string AdapterId = "yunet-v1";
    public const int InputSize = 640;
    public const string InputName = "input";
    public const float DefaultScoreThreshold = 0.6f;
    public const float DefaultNmsThreshold = 0.3f;
    public const int TopK = 5000;

    private static readonly int[] Strides = [8, 16, 32];

    private readonly IInferenceSession _session;
    private readonly IImageOperations _imageOperations;
    private readonly float _scoreThreshold;
    private readonly float _nmsThreshold;

    public YuNetFaceDetector(IInferenceSession session, IImageOperations imageOperations, float scoreThreshold = DefaultScoreThreshold, float nmsThreshold = DefaultNmsThreshold)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(imageOperations);
        _session = session;
        _imageOperations = imageOperations;
        _scoreThreshold = scoreThreshold;
        _nmsThreshold = nmsThreshold;
    }

    public string ModelVersion => $"{_session.Model.Id}@{_session.Model.Version}";

    public bool ProvidesEyeContours => false;

    public async Task<IReadOnlyList<DetectedFace>> DetectAsync(RgbImage image, CancellationToken ct)
    {
        var (input, scale) = PrepareInput(image, _imageOperations);
        var outputs = await _session.RunAsync([input], ct).ConfigureAwait(false);
        return Decode(outputs, _scoreThreshold, _nmsThreshold)
            .Select(f => new DetectedFace(
                new PixelBox(f.X / scale, f.Y / scale, (f.X + f.Width) / scale, (f.Y + f.Height) / scale),
                f.Score,
                f.Keypoints.Select(p => new Point2(p.X / scale, p.Y / scale)).ToArray(),
                null,
                null,
                null))
            .ToList();
    }

    /// <summary>Fit within 640 (scale = 640 / long side, sizes truncated), top-left on black, BGR float 0..255.</summary>
    public static (NamedTensor Input, float Scale) PrepareInput(RgbImage image, IImageOperations imageOperations)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(imageOperations);
        var scale = (double)InputSize / Math.Max(image.Width, image.Height);
        var width = Math.Max(1, (int)(image.Width * scale));
        var height = Math.Max(1, (int)(image.Height * scale));
        var resized = imageOperations.Resize(image, width, height, ResampleMode.Bilinear);
        const int plane = InputSize * InputSize;
        var data = new float[plane * 3];
        var pixels = resized.Pixels.Span;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var s = ((y * width) + x) * 3;
                var d = (y * InputSize) + x;
                data[d] = pixels[s + 2];
                data[plane + d] = pixels[s + 1];
                data[(2 * plane) + d] = pixels[s];
            }
        }

        return (NamedTensor.Float32(InputName, data, 1, 3, InputSize, InputSize), (float)scale);
    }

    /// <summary>A face in canvas pixels, as returned by <c>FaceDetectorYN::detect</c>.</summary>
    public sealed record CanvasFace(float X, float Y, float Width, float Height, IReadOnlyList<Point2> Keypoints, float Score);

    /// <summary>Port of <c>FaceDetectorYN::postProcess</c> (OpenCV 5.0) for the 640×640 canvas.</summary>
    public static IReadOnlyList<CanvasFace> Decode(IReadOnlyList<NamedTensor> outputs, float scoreThreshold, float nmsThreshold)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        if (outputs.Count != Strides.Length * 4)
        {
            throw new ArgumentException($"YuNet returns {Strides.Length * 4} outputs, got {outputs.Count}.", nameof(outputs));
        }

        var faces = new List<CanvasFace>();
        for (var i = 0; i < Strides.Length; i++)
        {
            var stride = Strides[i];
            var cols = InputSize / stride;
            var rows = InputSize / stride;
            var cls = outputs[i].AsFloat32().Span;
            var obj = outputs[i + Strides.Length].AsFloat32().Span;
            var bbox = outputs[i + (Strides.Length * 2)].AsFloat32().Span;
            var kps = outputs[i + (Strides.Length * 3)].AsFloat32().Span;
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    var idx = (r * cols) + c;
                    var clsScore = Math.Clamp(cls[idx], 0f, 1f);
                    var objScore = Math.Clamp(obj[idx], 0f, 1f);
                    var score = MathF.Sqrt(clsScore * objScore);
                    if (score < scoreThreshold)
                    {
                        continue;
                    }

                    var cx = (c + bbox[(idx * 4) + 0]) * stride;
                    var cy = (r + bbox[(idx * 4) + 1]) * stride;
                    var w = MathF.Exp(bbox[(idx * 4) + 2]) * stride;
                    var h = MathF.Exp(bbox[(idx * 4) + 3]) * stride;
                    var points = new Point2[5];
                    for (var n = 0; n < 5; n++)
                    {
                        points[n] = new Point2((kps[(idx * 10) + (2 * n)] + c) * stride, (kps[(idx * 10) + (2 * n) + 1] + r) * stride);
                    }

                    faces.Add(new CanvasFace(cx - (w / 2f), cy - (h / 2f), w, h, points, score));
                }
            }
        }

        return faces.Count > 1 ? Nms(faces, scoreThreshold, nmsThreshold) : faces;
    }

    /// <summary><c>cv::dnn::NMSBoxes</c> on integer rectangles (truncated as <c>Rect2i(int(x), …)</c>), stable score order.</summary>
    private static List<CanvasFace> Nms(List<CanvasFace> faces, float scoreThreshold, float nmsThreshold)
    {
        var order = Enumerable.Range(0, faces.Count)
            .Where(i => faces[i].Score > scoreThreshold)
            .OrderByDescending(i => faces[i].Score)
            .Take(TopK)
            .ToList();
        var rects = faces.Select(f => (X: (int)f.X, Y: (int)f.Y, W: (int)f.Width, H: (int)f.Height)).ToArray();
        var kept = new List<int>();
        foreach (var i in order)
        {
            var keep = true;
            foreach (var k in kept)
            {
                var a = rects[i];
                var b = rects[k];
                var iw = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
                var ih = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
                var intersection = iw > 0 && ih > 0 ? (double)iw * ih : 0;
                var union = ((double)a.W * a.H) + ((double)b.W * b.H) - intersection;
                if (union > 0 && intersection / union > nmsThreshold)
                {
                    keep = false;
                    break;
                }
            }

            if (keep)
            {
                kept.Add(i);
            }
        }

        return kept.Select(i => faces[i]).ToList();
    }
}
