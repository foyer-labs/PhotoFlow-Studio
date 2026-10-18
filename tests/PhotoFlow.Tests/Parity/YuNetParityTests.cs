// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Faces.Detection;
using PhotoFlow.Imaging;
using PhotoFlow.Inference;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Parity;

/// <summary>YuNet adapter against OpenCV 5.0 <c>FaceDetectorYN</c> (references from tools/lab/yunet_refs.py).</summary>
[Trait(TestCategory.Name, TestCategory.Parity)]
public sealed class YuNetParityTests
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ExecutionTarget.Cpu)]
    [InlineData(ExecutionTarget.Gpu)]
    public async Task Inputs_tensors_and_detections_match_opencv(ExecutionTarget target)
    {
        var folder = TestData.RequireDirectory("parity", "yunet");
        var indexPath = Path.Combine(folder, "index.json");
        if (!File.Exists(indexPath))
        {
            Assert.Skip($"Parity references missing: run tools/lab/yunet_refs.py ({indexPath}).");
        }

        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, Ct))!;
        var model = Path.Combine(TestData.RequireDirectory("models", "opencv-yunet-2023mar"), "face_detection_yunet_2023mar.onnx");
        var provider = new OnnxRuntimeInferenceProvider(new InferenceOptions(GpuRuntime.Directory));
        if (target == ExecutionTarget.Gpu && !provider.Detected.AvailableBackends.Contains(ExecutionBackend.Cuda))
        {
            Assert.Skip($"CUDA not available: {provider.GpuError}");
        }

        using var session = await provider.CreateSessionAsync(
            new ModelDescriptor("opencv-yunet-2023mar", ModelFunction.Faces, "YuNet", "2023mar", model, string.Empty, YuNetFaceDetector.AdapterId), target, Ct);
        var operations = new SkiaImageOperations();
        var threshold = index["score_threshold"]!.GetValue<float>();
        var nms = index["nms_threshold"]!.GetValue<float>();
        var failures = new List<string>();
        double maxTensor = 0, minIou = 1, maxScore = 0, maxPoint = 0;
        var matched = 0;

        foreach (var photo in index["photos"]!.AsArray().Select(p => p!))
        {
            var name = photo["image"]!.GetValue<string>();
            var image = new RgbImage(photo["width"]!.GetValue<int>(), photo["height"]!.GetValue<int>(), File.ReadAllBytes(Path.Combine(folder, name + ".rgb")));
            var (input, scale) = YuNetFaceDetector.PrepareInput(image, operations);
            Assert.Equal(photo["scale"]!.GetValue<double>(), scale, 6);
            Assert.True(ReadFloats(folder, $"{name}.yunet_input").AsSpan().SequenceEqual(input.AsFloat32().Span), $"{name}: input");

            var outputs = await session.RunAsync([input], Ct);
            for (var i = 0; i < outputs.Count; i++)
            {
                var expected = ReadFloats(folder, $"{name}.yunet_out{i}");
                var actual = outputs[i].AsFloat32().Span;
                for (var k = 0; k < expected.Length; k++)
                {
                    maxTensor = Math.Max(maxTensor, Math.Abs(actual[k] - expected[k]));
                }
            }

            var faces = YuNetFaceDetector.Decode(outputs, threshold, nms).ToList();
            foreach (var reference in photo["faces"]!.AsArray().Select(f => f!.AsArray().Select(v => v!.GetValue<float>()).ToArray()))
            {
                var box = new BoundingBox(reference[0], reference[1], reference[0] + reference[2], reference[1] + reference[3]);
                var best = faces.MaxBy(f => new BoundingBox(f.X, f.Y, f.X + f.Width, f.Y + f.Height).IntersectionOverUnion(box));
                var iou = best is null ? 0 : new BoundingBox(best.X, best.Y, best.X + best.Width, best.Y + best.Height).IntersectionOverUnion(box);
                if (best is null || iou < ParityTolerances.MinIou)
                {
                    if (Math.Abs(reference[14] - threshold) > ParityTolerances.ThresholdBand)
                    {
                        failures.Add($"{name}: face with score {reference[14]:F3} not found (IoU {iou:F3})");
                    }

                    continue;
                }

                faces.Remove(best);
                matched++;
                minIou = Math.Min(minIou, iou);
                maxScore = Math.Max(maxScore, Math.Abs(best.Score - reference[14]));
                var size = Math.Max(reference[2], reference[3]);
                for (var n = 0; n < 5; n++)
                {
                    var d = Math.Sqrt(Math.Pow(best.Keypoints[n].X - reference[4 + (2 * n)], 2) + Math.Pow(best.Keypoints[n].Y - reference[5 + (2 * n)], 2));
                    maxPoint = Math.Max(maxPoint, d / size);
                }
            }

            failures.AddRange(faces.Where(f => Math.Abs(f.Score - threshold) > ParityTolerances.ThresholdBand).Select(f => $"{name}: unexpected face {f.Score:F3}"));
        }

        var report = new JsonObject
        {
            ["matched"] = matched,
            ["max_abs_diff_tensors"] = maxTensor,
            ["min_iou"] = minIou,
            ["max_score_diff"] = maxScore,
            ["max_keypoint_diff_relative_to_face"] = maxPoint,
            ["failures"] = new JsonArray(failures.Select(f => (JsonNode)f!).ToArray()),
            ["generated_at"] = DateTimeOffset.Now.ToString("O"),
        };
        var reports = Path.Combine(TestData.Root!, "parity", "reports");
        Directory.CreateDirectory(reports);
        await File.WriteAllTextAsync(Path.Combine(reports, $"yunet{(target == ExecutionTarget.Gpu ? "-cuda" : string.Empty)}.json"), report.ToJsonString(ReportJson), Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToJsonString(ReportJson));

        Assert.True(failures.Count == 0, string.Join('\n', failures));
        Assert.True(maxTensor <= FaceTolerances.Distances, $"tensors: max |Δ| {maxTensor:E2}");
        Assert.True(maxScore <= ParityTolerances.MaxScoreDiff, $"score: max |Δ| {maxScore:E2}");
        Assert.True(maxPoint <= FaceTolerances.Landmarks, $"keypoints: max |Δ| / face {maxPoint:E2}");
    }

    private static float[] ReadFloats(string folder, string name) =>
        MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(folder, name + ".bin"))).ToArray();
}
