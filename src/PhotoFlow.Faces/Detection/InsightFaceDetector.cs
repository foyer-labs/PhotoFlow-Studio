// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;

namespace PhotoFlow.Faces.Detection;

/// <summary>
/// Adapter for the insightface buffalo pack: SCRFD (<c>det_10g</c>) for boxes and 5 keypoints, then <c>2d106det</c>
/// for 106 landmarks and the eye contours. Post-processing is a port of insightface 2.1 (<c>SCRFD.detect</c>,
/// <c>Landmark.get</c>). Input preparation uses antialiased resampling (Pillow semantics) instead of
/// <c>cv2.resize</c> / <c>cv2.warpAffine</c>, which point-sample when large previews are reduced; the Python references
/// (tools/lab/face_refs.py) prepare the inputs the same way.
/// </summary>
public sealed class InsightFaceDetector : IFaceDetector
{
    public const string AdapterId = "insightface-buffalo-v1";
    public const int DetectorSize = 640;
    public const int LandmarkSize = 192;
    public const string DetectorInputName = "input.1";
    public const string LandmarkInputName = "data";
    public const float DefaultScoreThreshold = 0.5f;
    public const float DefaultNmsThreshold = 0.4f;

    /// <summary>Faces smaller than this (longest side, pixels) get no landmarks: too few pixels for the eyes.</summary>
    public const float DefaultLandmarkMinFaceSize = 32;

    /// <summary>2d106det eye outlines in <see cref="EyeContour"/> order, determined on the lab photos (face_refs.py).</summary>
    public static readonly int[] LeftEyeIndices = [35, 36, 33, 37, 39, 42, 40, 41];

    public static readonly int[] RightEyeIndices = [89, 90, 87, 91, 93, 96, 94, 95];

    private const int LandmarkCount = 106;
    private static readonly int[] Strides = [8, 16, 32];
    private const int AnchorsPerLocation = 2;

    private readonly IInferenceSession _detector;
    private readonly IInferenceSession? _landmarks;
    private readonly IImageOperations _imageOperations;
    private readonly float _scoreThreshold;
    private readonly float _nmsThreshold;
    private readonly float _landmarkMinFaceSize;

    public InsightFaceDetector(
        IInferenceSession detector,
        IInferenceSession? landmarks,
        IImageOperations imageOperations,
        float scoreThreshold = DefaultScoreThreshold,
        float nmsThreshold = DefaultNmsThreshold,
        float landmarkMinFaceSize = DefaultLandmarkMinFaceSize)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(imageOperations);
        _detector = detector;
        _landmarks = landmarks;
        _imageOperations = imageOperations;
        _scoreThreshold = scoreThreshold;
        _nmsThreshold = nmsThreshold;
        _landmarkMinFaceSize = landmarkMinFaceSize;
    }

    public string ModelVersion => $"{_detector.Model.Id}@{_detector.Model.Version}";

    public bool ProvidesEyeContours => _landmarks is not null;

    public async Task<IReadOnlyList<DetectedFace>> DetectAsync(RgbImage image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (input, scale) = PrepareDetectorInput(image, _imageOperations);
        var outputs = await _detector.RunAsync([input], ct).ConfigureAwait(false);
        var candidates = Decode(outputs, scale, _scoreThreshold, _nmsThreshold);

        var landmarks = new IReadOnlyList<Point2>?[candidates.Count];
        if (_landmarks is not null)
        {
            // One face per run: the model declares a fixed output batch of 1 ({1, 212}).
            for (var i = 0; i < candidates.Count; i++)
            {
                if (Math.Max(candidates[i].Box.Width, candidates[i].Box.Height) < _landmarkMinFaceSize)
                {
                    continue;
                }

                var crop = PrepareLandmarkBatch(image, [candidates[i].Box], _imageOperations);
                var result = await _landmarks.RunAsync([crop], ct).ConfigureAwait(false);
                landmarks[i] = DecodeLandmarks(result[0].AsFloat32().Span, candidates[i].Box);
            }
        }

        var faces = new List<DetectedFace>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var points = landmarks[i];
            faces.Add(new DetectedFace(
                candidates[i].Box,
                candidates[i].Score,
                candidates[i].Keypoints,
                points,
                points is null ? null : Contour(points, LeftEyeIndices),
                points is null ? null : Contour(points, RightEyeIndices)));
        }

        return faces;
    }

    /// <summary>Letterbox as <c>SCRFD.detect</c> (top-left aligned, zero padded), RGB, (x − 127.5) / 128.</summary>
    public static (NamedTensor Input, double Scale) PrepareDetectorInput(RgbImage image, IImageOperations imageOperations)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(imageOperations);
        var ratio = (double)image.Height / image.Width;
        int width, height;
        if (ratio > 1.0)
        {
            height = DetectorSize;
            width = (int)(height / ratio);
        }
        else
        {
            width = DetectorSize;
            height = (int)(width * ratio);
        }

        var scale = (double)height / image.Height;
        var resized = imageOperations.Resize(image, Math.Max(1, width), Math.Max(1, height), ResampleMode.Bilinear);
        const int plane = DetectorSize * DetectorSize;
        var data = new float[plane * 3];
        Array.Fill(data, (0f - 127.5f) / 128f);
        var pixels = resized.Pixels.Span;
        for (var y = 0; y < resized.Height; y++)
        {
            for (var x = 0; x < resized.Width; x++)
            {
                var s = ((y * resized.Width) + x) * 3;
                var d = (y * DetectorSize) + x;
                data[d] = (pixels[s] - 127.5f) / 128f;
                data[plane + d] = (pixels[s + 1] - 127.5f) / 128f;
                data[(2 * plane) + d] = (pixels[s + 2] - 127.5f) / 128f;
            }
        }

        return (NamedTensor.Float32(DetectorInputName, data, 1, 3, DetectorSize, DetectorSize), scale);
    }

    /// <summary>Port of the decoding in <c>SCRFD.forward</c> + <c>SCRFD.detect</c>: anchors, distances, score sort, NMS.</summary>
    public static IReadOnlyList<(PixelBox Box, float Score, IReadOnlyList<Point2> Keypoints)> Decode(
        IReadOnlyList<NamedTensor> outputs, double scale, float scoreThreshold, float nmsThreshold)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        if (outputs.Count != Strides.Length * 3)
        {
            throw new ArgumentException($"SCRFD returns {Strides.Length * 3} outputs, got {outputs.Count}.", nameof(outputs));
        }

        var invScale = (float)scale;
        var candidates = new List<(PixelBox Box, float Score, Point2[] Keypoints)>();
        for (var level = 0; level < Strides.Length; level++)
        {
            var stride = Strides[level];
            var size = DetectorSize / stride;
            var count = size * size * AnchorsPerLocation;
            var scores = outputs[level].AsFloat32().Span;
            var distances = outputs[level + Strides.Length].AsFloat32().Span;
            var keypoints = outputs[level + (2 * Strides.Length)].AsFloat32().Span;
            if (scores.Length != count || distances.Length != count * 4 || keypoints.Length != count * 10)
            {
                throw new ArgumentException($"Unexpected SCRFD output sizes at stride {stride}.", nameof(outputs));
            }

            for (var a = 0; a < count; a++)
            {
                var score = scores[a];
                if (score < scoreThreshold)
                {
                    continue;
                }

                var location = a / AnchorsPerLocation;
                float cx = location % size * stride, cy = location / size * stride;
                var box = new PixelBox(
                    (cx - (distances[a * 4] * stride)) / invScale,
                    (cy - (distances[(a * 4) + 1] * stride)) / invScale,
                    (cx + (distances[(a * 4) + 2] * stride)) / invScale,
                    (cy + (distances[(a * 4) + 3] * stride)) / invScale);
                var points = new Point2[5];
                for (var k = 0; k < 5; k++)
                {
                    points[k] = new Point2(
                        (cx + (keypoints[(a * 10) + (2 * k)] * stride)) / invScale,
                        (cy + (keypoints[(a * 10) + (2 * k) + 1] * stride)) / invScale);
                }

                candidates.Add((box, score, points));
            }
        }

        candidates.Sort(static (p, q) => q.Score.CompareTo(p.Score));
        var kept = new List<(PixelBox, float, IReadOnlyList<Point2>)>();
        var suppressed = new bool[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            if (suppressed[i])
            {
                continue;
            }

            var a = candidates[i].Box;
            kept.Add((a, candidates[i].Score, candidates[i].Keypoints));
            var areaA = (a.X2 - a.X1 + 1) * (a.Y2 - a.Y1 + 1);
            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (suppressed[j])
                {
                    continue;
                }

                var b = candidates[j].Box;
                var w = Math.Max(0f, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1) + 1);
                var h = Math.Max(0f, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1) + 1);
                var intersection = w * h;
                var areaB = (b.X2 - b.X1 + 1) * (b.Y2 - b.Y1 + 1);
                if (intersection / (areaA + areaB - intersection) > nmsThreshold)
                {
                    suppressed[j] = true;
                }
            }
        }

        return kept;
    }

    /// <summary>
    /// Source box equivalent to insightface's <c>warpAffine</c> crop (center of the box, scale 192 / (1.5 × longest side)),
    /// aligned to its pixel-center convention.
    /// </summary>
    public static SourceBox LandmarkCropBox(PixelBox box, out float scale)
    {
        var w = box.X2 - box.X1;
        var h = box.Y2 - box.Y1;
        var cx = (box.X2 + box.X1) / 2f;
        var cy = (box.Y2 + box.Y1) / 2f;
        scale = LandmarkSize / (Math.Max(w, h) * 1.5f);
        var x0 = cx + 0.5f - ((LandmarkSize / 2f) + 0.5f) / scale;
        var y0 = cy + 0.5f - ((LandmarkSize / 2f) + 0.5f) / scale;
        return new SourceBox(x0, y0, x0 + (LandmarkSize / scale), y0 + (LandmarkSize / scale));
    }

    /// <summary>Zero-padded integer crop around the crop box, then antialiased resize of the fractional box to 192×192, RGB 0..255.</summary>
    public static NamedTensor PrepareLandmarkBatch(RgbImage image, IReadOnlyList<PixelBox> boxes, IImageOperations imageOperations)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(imageOperations);
        const int plane = LandmarkSize * LandmarkSize;
        var data = new float[boxes.Count * plane * 3];
        for (var n = 0; n < boxes.Count; n++)
        {
            var crop = LandmarkCropBox(boxes[n], out _);
            var ix0 = (int)Math.Floor(crop.X0);
            var iy0 = (int)Math.Floor(crop.Y0);
            var ix1 = (int)Math.Ceiling(crop.X1);
            var iy1 = (int)Math.Ceiling(crop.Y1);
            var region = CropWithPadding(image, ix0, iy0, ix1 - ix0, iy1 - iy0);
            var relative = new SourceBox(crop.X0 - ix0, crop.Y0 - iy0, crop.X1 - ix0, crop.Y1 - iy0);
            var resized = imageOperations.Resize(region, relative, LandmarkSize, LandmarkSize, ResampleMode.Bilinear);
            var pixels = resized.Pixels.Span;
            var offset = n * plane * 3;
            for (var i = 0; i < plane; i++)
            {
                data[offset + i] = pixels[i * 3];
                data[offset + plane + i] = pixels[(i * 3) + 1];
                data[offset + (2 * plane) + i] = pixels[(i * 3) + 2];
            }
        }

        return NamedTensor.Float32(LandmarkInputName, data, boxes.Count, 3, LandmarkSize, LandmarkSize);
    }

    /// <summary>Port of <c>Landmark.get</c> after the model: (p + 1) × 96 in crop pixels, then the inverse similarity transform.</summary>
    public static IReadOnlyList<Point2> DecodeLandmarks(ReadOnlySpan<float> output, PixelBox box)
    {
        if (output.Length != LandmarkCount * 2)
        {
            throw new ArgumentException($"2d106det returns {LandmarkCount * 2} values per face, got {output.Length}.", nameof(output));
        }

        var w = box.X2 - box.X1;
        var h = box.Y2 - box.Y1;
        var cx = (box.X2 + box.X1) / 2f;
        var cy = (box.Y2 + box.Y1) / 2f;
        var s = LandmarkSize / (Math.Max(w, h) * 1.5f);
        double tx = (LandmarkSize / 2f) - (cx * s);
        double ty = (LandmarkSize / 2f) - (cy * s);

        // cv2.invertAffineTransform of [[s, 0, tx], [0, s, ty]].
        var inverse = 1.0 / ((double)s * s);
        var a11 = s * inverse;
        var b1 = -a11 * tx;
        var b2 = -a11 * ty;
        var points = new Point2[LandmarkCount];
        for (var i = 0; i < LandmarkCount; i++)
        {
            var px = (output[2 * i] + 1f) * (LandmarkSize / 2);
            var py = (output[(2 * i) + 1] + 1f) * (LandmarkSize / 2);
            points[i] = new Point2((float)((a11 * px) + b1), (float)((a11 * py) + b2));
        }

        return points;
    }

    private static EyeContour Contour(IReadOnlyList<Point2> landmarks, int[] indices) =>
        new(indices.Select(i => landmarks[i]).ToArray());

    private static RgbImage CropWithPadding(RgbImage image, int x0, int y0, int width, int height)
    {
        var output = new byte[width * height * 3];
        var source = image.Pixels.Span;
        var sx0 = Math.Max(x0, 0);
        var sx1 = Math.Min(x0 + width, image.Width);
        if (sx1 > sx0)
        {
            for (var y = Math.Max(y0, 0); y < Math.Min(y0 + height, image.Height); y++)
            {
                source.Slice(((y * image.Width) + sx0) * 3, (sx1 - sx0) * 3)
                    .CopyTo(output.AsSpan((((y - y0) * width) + (sx0 - x0)) * 3));
            }
        }

        return new RgbImage(width, height, output);
    }
}
