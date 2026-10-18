// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Faces.Detection;
using PhotoFlow.Imaging;
using PhotoFlow.Inference;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Parity;

/// <summary>
/// buffalo_l adapter (SCRFD det_10g + 2d106det) against insightface 2.1 (spec §7, §14). References from
/// tools/lab/face_refs.py in PHOTOFLOW_TESTDATA/parity/faces: same input preparation, insightface post-processing.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Parity)]
public sealed class FaceParityTests
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Model_inputs_are_bit_exact()
    {
        var (folder, index) = await LoadAsync();
        var operations = new SkiaImageOperations();
        foreach (var photo in Photos(index))
        {
            var image = ReadImage(folder, photo);
            var (input, scale) = InsightFaceDetector.PrepareDetectorInput(image, operations);
            Assert.Equal(photo["det_scale"]!.GetValue<double>(), scale, 12);
            Assert.True(ReadFloats(folder, $"{Name(photo)}.det_input").AsSpan().SequenceEqual(input.AsFloat32().Span), $"{Name(photo)}: detector input");

            var faces = Faces(photo).ToList();
            for (var k = 0; k < faces.Count; k++)
            {
                var batch = InsightFaceDetector.PrepareLandmarkBatch(image, [Box(faces[k])], operations);
                Assert.True(ReadFloats(folder, $"{Name(photo)}.f{k}.lmk_input").AsSpan().SequenceEqual(batch.AsFloat32().Span), $"{Name(photo)} face {k}: landmark input");
            }
        }
    }

    [Theory]
    [InlineData(ExecutionTarget.Cpu)]
    [InlineData(ExecutionTarget.Gpu)]
    public async Task Tensor_level_matches_insightface(ExecutionTarget target)
    {
        var (folder, index) = await LoadAsync();
        var (detector, landmarks) = await SessionsAsync(target);
        using var _ = detector;
        using var __ = landmarks;
        double scores = 0, distances = 0, points = 0;
        foreach (var photo in Photos(index))
        {
            var input = NamedTensor.Float32(InsightFaceDetector.DetectorInputName, ReadFloats(folder, $"{Name(photo)}.det_input"), 1, 3, 640, 640);
            var outputs = await detector.RunAsync([input], Ct);
            for (var i = 0; i < outputs.Count; i++)
            {
                var diff = MaxAbsDiff(outputs[i].AsFloat32().Span, ReadFloats(folder, $"{Name(photo)}.det_out{i}"));
                if (i < 3)
                {
                    scores = Math.Max(scores, diff);
                }
                else
                {
                    distances = Math.Max(distances, diff);
                }
            }

            for (var k = 0; k < Faces(photo).Count(); k++)
            {
                var lmkInput = NamedTensor.Float32(InsightFaceDetector.LandmarkInputName, ReadFloats(folder, $"{Name(photo)}.f{k}.lmk_input"), 1, 3, 192, 192);
                var result = await landmarks.RunAsync([lmkInput], Ct);
                points = Math.Max(points, MaxAbsDiff(result[0].AsFloat32().Span, ReadFloats(folder, $"{Name(photo)}.f{k}.lmk_out")));
            }
        }

        WriteReport($"faces-tensors{Suffix(target)}", new JsonObject
        {
            ["max_abs_diff_scores"] = scores,
            ["max_abs_diff_distances_stride_units"] = distances,
            ["max_abs_diff_landmarks_normalized"] = points,
            ["tolerance_scores"] = FaceTolerances.Scores,
            ["tolerance_distances"] = FaceTolerances.Distances,
            ["tolerance_landmarks"] = FaceTolerances.RawLandmarks,
        });
        Assert.True(scores <= FaceTolerances.Scores, $"scores: max |Δ| {scores:E2}");
        Assert.True(distances <= FaceTolerances.Distances, $"distances: max |Δ| {distances:E2}");
        Assert.True(points <= FaceTolerances.RawLandmarks, $"landmarks: max |Δ| {points:E2}");
    }

    [Theory]
    [InlineData(ExecutionTarget.Cpu)]
    [InlineData(ExecutionTarget.Gpu)]
    public async Task Detection_level_matches_insightface(ExecutionTarget target)
    {
        var (folder, index) = await LoadAsync();
        var (detectorSession, landmarkSession) = await SessionsAsync(target);
        using var _ = detectorSession;
        using var __ = landmarkSession;
        var threshold = index["det_threshold"]!.GetValue<float>();
        var detector = new InsightFaceDetector(detectorSession, landmarkSession, new SkiaImageOperations(), threshold, index["nms_threshold"]!.GetValue<float>(), landmarkMinFaceSize: 0);
        var failures = new List<string>();
        double minIou = 1, maxScore = 0, maxLandmarks = 0, maxEyeRatio = 0;
        int matched = 0, tolerated = 0;

        foreach (var photo in Photos(index))
        {
            var image = ReadImage(folder, photo);
            var actual = (await detector.DetectAsync(image, Ct)).ToList();
            foreach (var reference in Faces(photo).OrderByDescending(f => f["score"]!.GetValue<float>()))
            {
                var box = Box(reference).ToNormalized(image.Width, image.Height);
                var score = reference["score"]!.GetValue<float>();
                var best = actual.OrderByDescending(f => f.Box.ToNormalized(image.Width, image.Height).IntersectionOverUnion(box)).FirstOrDefault();
                var iou = best?.Box.ToNormalized(image.Width, image.Height).IntersectionOverUnion(box) ?? 0;
                if (best is null || iou < ParityTolerances.MinIou)
                {
                    if (Math.Abs(score - threshold) <= ParityTolerances.ThresholdBand)
                    {
                        tolerated++;
                    }
                    else
                    {
                        failures.Add($"{Name(photo)}: face with score {score:F3} not found (best IoU {iou:F3})");
                    }

                    continue;
                }

                actual.Remove(best);
                matched++;
                minIou = Math.Min(minIou, iou);
                maxScore = Math.Max(maxScore, Math.Abs(best.Score - score));
                var expectedPoints = reference["landmarks"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
                var size = Math.Max(best.Box.Width, best.Box.Height);
                for (var i = 0; i < best.Landmarks!.Count; i++)
                {
                    var d = Math.Sqrt(Math.Pow(best.Landmarks[i].X - expectedPoints[2 * i], 2) + Math.Pow(best.Landmarks[i].Y - expectedPoints[(2 * i) + 1], 2));
                    maxLandmarks = Math.Max(maxLandmarks, d / size);
                }

                var ratios = reference["eye_ratio"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
                maxEyeRatio = Math.Max(maxEyeRatio, Math.Max(Math.Abs(best.LeftEye!.AspectRatio - ratios[0]), Math.Abs(best.RightEye!.AspectRatio - ratios[1])));
            }

            foreach (var extra in actual.Where(f => Math.Abs(f.Score - threshold) > ParityTolerances.ThresholdBand))
            {
                failures.Add($"{Name(photo)}: unexpected face with score {extra.Score:F3}");
            }
        }

        WriteReport($"faces-detections{Suffix(target)}", new JsonObject
        {
            ["matched"] = matched,
            ["tolerated_unmatched"] = tolerated,
            ["min_iou"] = minIou,
            ["max_score_diff"] = maxScore,
            ["max_landmark_diff_relative_to_face"] = maxLandmarks,
            ["max_eye_ratio_diff"] = maxEyeRatio,
            ["failures"] = new JsonArray(failures.Select(f => (JsonNode)f!).ToArray()),
        });
        Assert.True(failures.Count == 0, string.Join('\n', failures));
        Assert.True(maxScore <= ParityTolerances.MaxScoreDiff, $"score: max |Δ| {maxScore:E2}");
        Assert.True(maxLandmarks <= FaceTolerances.Landmarks, $"landmarks: max |Δ| / face {maxLandmarks:E2}");
        Assert.True(maxEyeRatio <= FaceTolerances.EyeRatio, $"eye ratio: max |Δ| {maxEyeRatio:E2}");
    }

    private static async Task<(string Folder, JsonNode Index)> LoadAsync()
    {
        var folder = TestData.RequireDirectory("parity", "faces");
        var path = Path.Combine(folder, "index.json");
        if (!File.Exists(path))
        {
            Assert.Skip($"Parity references missing: run tools/lab/face_refs.py ({path}).");
        }

        return (folder, JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!);
    }

    private static async Task<(IInferenceSession Detector, IInferenceSession Landmarks)> SessionsAsync(ExecutionTarget target)
    {
        var models = TestData.RequireDirectory("models", "insightface-buffalo-l");
        var provider = new OnnxRuntimeInferenceProvider(new InferenceOptions(GpuRuntime.Directory));
        if (target == ExecutionTarget.Gpu && !provider.Detected.AvailableBackends.Contains(ExecutionBackend.Cuda))
        {
            Assert.Skip($"CUDA not available: {provider.GpuError}");
        }

        ModelDescriptor Descriptor(string file) =>
            new("insightface-buffalo-l", ModelFunction.Faces, file, "model-zoo", Path.Combine(models, file), string.Empty, InsightFaceDetector.AdapterId);

        var detector = await provider.CreateSessionAsync(Descriptor("det_10g.onnx"), target, Ct);
        var landmarks = await provider.CreateSessionAsync(Descriptor("2d106det.onnx"), target, Ct);
        Assert.Equal(target == ExecutionTarget.Gpu ? ExecutionBackend.Cuda : ExecutionBackend.Cpu, detector.Backend);
        return (detector, landmarks);
    }

    private static IEnumerable<JsonNode> Photos(JsonNode index) => index["photos"]!.AsArray().Select(p => p!);

    private static IEnumerable<JsonNode> Faces(JsonNode photo) => photo["faces"]!.AsArray().Select(f => f!);

    private static string Name(JsonNode photo) => photo["image"]!.GetValue<string>();

    private static PixelBox Box(JsonNode face)
    {
        var b = face["box"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
        return new PixelBox(b[0], b[1], b[2], b[3]);
    }

    private static RgbImage ReadImage(string folder, JsonNode photo) =>
        new(photo["width"]!.GetValue<int>(), photo["height"]!.GetValue<int>(), File.ReadAllBytes(Path.Combine(folder, Name(photo) + ".rgb")));

    private static float[] ReadFloats(string folder, string name) =>
        MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(folder, name + ".bin"))).ToArray();

    private static double MaxAbsDiff(ReadOnlySpan<float> actual, ReadOnlySpan<float> expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        double max = 0;
        for (var i = 0; i < actual.Length; i++)
        {
            max = Math.Max(max, Math.Abs(actual[i] - expected[i]));
        }

        return max;
    }

    private static string Suffix(ExecutionTarget target) => target == ExecutionTarget.Gpu ? "-cuda" : string.Empty;

    private static void WriteReport(string name, JsonObject report)
    {
        var folder = Path.Combine(TestData.Root!, "parity", "reports");
        Directory.CreateDirectory(folder);
        report["generated_at"] = DateTimeOffset.Now.ToString("O");
        File.WriteAllText(Path.Combine(folder, name + ".json"), report.ToJsonString(ReportJson));
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToJsonString(ReportJson));
    }
}

/// <summary>Face parity tolerances (spec §14).</summary>
public static class FaceTolerances
{
    /// <summary>SCRFD scores (already sigmoid).</summary>
    public const double Scores = 5e-3;

    /// <summary>SCRFD box and keypoint distances, in stride units.</summary>
    public const double Distances = 5e-3;

    /// <summary>2d106det raw output (crop coordinates normalized to −1..1).</summary>
    public const double RawLandmarks = 5e-4;

    /// <summary>Final landmarks, distance relative to the longest side of the face.</summary>
    public const double Landmarks = 2e-3;

    /// <summary>Eye aspect ratio.</summary>
    public const double EyeRatio = 5e-3;
}
