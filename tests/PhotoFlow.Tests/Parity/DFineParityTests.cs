// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoFlow.Analysis.Detectors;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Imaging;
using PhotoFlow.Inference;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Parity;

/// <summary>
/// D-FINE in C# (ONNX Runtime CPU, via IInferenceProvider) against the original PyTorch model, on the two
/// levels of spec §14. References come from tools/lab/parity_refs.py and live in PHOTOFLOW_TESTDATA.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Parity)]
public sealed class DFineParityTests
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    [Theory]
    [InlineData("s", "export", ExecutionTarget.Cpu)]
    [InlineData("s", "onnx-community", ExecutionTarget.Cpu)]
    [InlineData("m", "export", ExecutionTarget.Cpu)]
    [InlineData("m", "onnx-community", ExecutionTarget.Cpu)]
    [InlineData("s", "onnx-community", ExecutionTarget.Gpu)]
    [InlineData("m", "onnx-community", ExecutionTarget.Gpu)]
    public async Task Tensor_level_matches_pytorch(string variant, string source, ExecutionTarget target)
    {
        var (references, index, detector, session) = await PrepareAsync(variant, source, target);
        using var _ = session;
        var rows = new JsonArray();
        double worstScores = 0, worstBoxes = 0;
        int reorderedImages = 0, movedQueries = 0;

        foreach (var item in Items(index))
        {
            var input = NamedTensor.Float32(DFineDetector.InputName, ReadFloats(references, item, "input"), 1, 3, 640, 640);
            var (logits, boxes) = await detector.RunRawAsync(input, TestContext.Current.CancellationToken);
            var comparison = QueryMatcher.Compare(
                Sigmoid(logits.AsFloat32().Span), boxes.AsFloat32().ToArray(),
                Sigmoid(ReadFloats(references, item, "logits")), ReadFloats(references, item, "boxes"),
                classes: logits.Shape[2]);
            worstScores = Math.Max(worstScores, comparison.MaxScoreDiff);
            worstBoxes = Math.Max(worstBoxes, comparison.MaxBoxDiff);
            movedQueries += comparison.MovedQueries;
            reorderedImages += comparison.MovedQueries > 0 ? 1 : 0;
            rows.Add(new JsonObject
            {
                ["image"] = item,
                ["max_abs_diff_scores"] = comparison.MaxScoreDiff,
                ["max_abs_diff_boxes"] = comparison.MaxBoxDiff,
                ["moved_queries"] = comparison.MovedQueries,
            });
        }

        WriteReport(variant, source + Suffix(target), "tensors", new JsonObject
        {
            ["criteria"] = "queries matched by optimal assignment (boxes + scores), then |Δ score (sigmoid)| and |Δ box|",
            ["tolerance_scores"] = ParityTolerances.Scores,
            ["tolerance_boxes"] = ParityTolerances.Boxes,
            ["max_abs_diff_scores"] = worstScores,
            ["max_abs_diff_boxes"] = worstBoxes,
            ["images_with_reordered_queries"] = reorderedImages,
            ["moved_queries"] = movedQueries,
            ["images"] = rows,
        });
        Assert.True(worstBoxes <= ParityTolerances.Boxes, $"boxes: max |Δ| {worstBoxes:E2} > {ParityTolerances.Boxes:E0}");
        Assert.True(worstScores <= ParityTolerances.Scores, $"scores: max |Δ| {worstScores:E2} > {ParityTolerances.Scores:E0}");
    }

    private static float[] Sigmoid(ReadOnlySpan<float> logits)
    {
        var scores = new float[logits.Length];
        for (var i = 0; i < logits.Length; i++)
        {
            scores[i] = 1f / (1f + MathF.Exp(-logits[i]));
        }

        return scores;
    }

    [Theory]
    [InlineData("s", "export", ExecutionTarget.Cpu)]
    [InlineData("s", "onnx-community", ExecutionTarget.Cpu)]
    [InlineData("m", "export", ExecutionTarget.Cpu)]
    [InlineData("m", "onnx-community", ExecutionTarget.Cpu)]
    [InlineData("s", "onnx-community", ExecutionTarget.Gpu)]
    [InlineData("m", "onnx-community", ExecutionTarget.Gpu)]
    public async Task Detection_level_matches_pytorch(string variant, string source, ExecutionTarget target)
    {
        var (references, index, detector, session) = await PrepareAsync(variant, source, target);
        using var _ = session;
        var root = TestData.Root!;
        var imageOperations = new SkiaImageOperations();
        var rows = new JsonArray();
        var failures = new List<string>();
        double minIou = 1, maxScoreDiff = 0;
        int matched = 0, tolerated = 0;

        foreach (var item in Items(index))
        {
            var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(references, item + ".json")))!;
            var expected = meta["detections"]!.AsArray().Select(d => new Detection(
                d!["class_id"]!.GetValue<int>(),
                d["label"]!.GetValue<string>(),
                d["score"]!.GetValue<float>(),
                Box(d["box"]!.AsArray()))).ToList();

            await using var stream = File.OpenRead(Path.Combine(root, meta["image"]!.GetValue<string>()));
            var image = await imageOperations.DecodeAsync(stream, TestContext.Current.CancellationToken);

            // Preprocessing fidelity (our Pillow-compatible resampler vs the official processor), reported only.
            var ourInput = DFineDetector.Preprocess(imageOperations.Resize(image, 640, 640, ResampleMode.Bilinear));
            var dInput = MaxAbsDiff(ourInput.AsFloat32().Span, ReadFloats(references, item, "input"));

            var actual = await detector.DetectAsync(image, TestContext.Current.CancellationToken);
            var comparison = DetectionMatcher.Compare(expected, actual, threshold: index["threshold"]!.GetValue<float>());
            matched += comparison.Matched;
            tolerated += comparison.ToleratedUnmatched;
            minIou = Math.Min(minIou, comparison.MinIou);
            maxScoreDiff = Math.Max(maxScoreDiff, comparison.MaxScoreDiff);
            failures.AddRange(comparison.Failures.Select(f => $"{item}: {f}"));
            rows.Add(new JsonObject
            {
                ["image"] = item,
                ["expected"] = expected.Count,
                ["actual"] = actual.Count,
                ["matched"] = comparison.Matched,
                ["tolerated_unmatched"] = comparison.ToleratedUnmatched,
                ["min_iou"] = comparison.MinIou,
                ["max_score_diff"] = comparison.MaxScoreDiff,
                ["max_abs_diff_input"] = dInput,
                ["failures"] = new JsonArray(comparison.Failures.Select(f => (JsonNode)f!).ToArray()),
            });
        }

        WriteReport(variant, source + Suffix(target), "detections", new JsonObject
        {
            ["criteria"] = $"per class, IoU >= {ParityTolerances.MinIou}, |Δscore| <= {ParityTolerances.MaxScoreDiff}, unmatched only within ±{ParityTolerances.ThresholdBand} of the threshold",
            ["matched"] = matched,
            ["tolerated_unmatched"] = tolerated,
            ["min_iou"] = minIou,
            ["max_score_diff"] = maxScoreDiff,
            ["images"] = rows,
        });
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    private static string Suffix(ExecutionTarget target) => target == ExecutionTarget.Gpu ? "-cuda" : string.Empty;

    private static async Task<(string References, JsonNode Index, DFineDetector Detector, IInferenceSession Session)> PrepareAsync(string variant, string source, ExecutionTarget target)
    {
        var references = TestData.RequireDirectory("parity", $"dfine-{variant}");
        var indexPath = Path.Combine(references, "index.json");
        if (!File.Exists(indexPath))
        {
            Assert.Skip($"Parity references missing: run tools/lab/parity_refs.py ({indexPath}).");
        }

        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, TestContext.Current.CancellationToken))!;
        var model = index["onnx_files"]?[source];
        if (model is null)
        {
            Assert.Skip($"ONNX file '{source}' not listed in {indexPath}.");
        }

        var descriptor = new ModelDescriptor(
            $"dfine-{variant}-obj2coco-{source}",
            ModelFunction.ObjectDetection,
            $"D-FINE {variant.ToUpperInvariant()} ({source})",
            index["revision"]!.GetValue<string>(),
            model["path"]!.GetValue<string>(),
            model["sha256"]!.GetValue<string>(),
            DFineDetector.AdapterId);

        var provider = new OnnxRuntimeInferenceProvider(new InferenceOptions(GpuRuntime.Directory));
        if (target == ExecutionTarget.Gpu && !provider.Detected.AvailableBackends.Contains(ExecutionBackend.Cuda))
        {
            Assert.Skip($"CUDA not available: {provider.GpuError}");
        }

        var session = await provider.CreateSessionAsync(descriptor, target, TestContext.Current.CancellationToken);
        Assert.Equal(target == ExecutionTarget.Gpu ? ExecutionBackend.Cuda : ExecutionBackend.Cpu, session.Backend);
        return (references, index, new DFineDetector(session, new SkiaImageOperations(), index["threshold"]!.GetValue<float>()), session);
    }

    private static IEnumerable<string> Items(JsonNode index) => index["items"]!.AsArray().Select(i => i!.GetValue<string>());

    private static float[] ReadFloats(string folder, string item, string kind)
    {
        var bytes = File.ReadAllBytes(Path.Combine(folder, $"{item}.{kind}.bin"));
        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }

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

    private static BoundingBox Box(JsonArray values) =>
        new(values[0]!.GetValue<float>(), values[1]!.GetValue<float>(), values[2]!.GetValue<float>(), values[3]!.GetValue<float>());

    private static void WriteReport(string variant, string source, string level, JsonObject report)
    {
        var folder = Path.Combine(TestData.Root!, "parity", "reports");
        Directory.CreateDirectory(folder);
        report["variant"] = variant;
        report["source"] = source;
        report["level"] = level;
        report["generated_at"] = DateTimeOffset.Now.ToString("O");
        File.WriteAllText(Path.Combine(folder, $"dfine-{variant}-{source}-{level}.json"), report.ToJsonString(ReportJson));
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToJsonString(ReportJson));
    }
}

/// <summary>The GPU runtime installed for the current user (PHOTOFLOW_GPU_RUNTIME overrides it).</summary>
public static class GpuRuntime
{
    public static string Directory =>
        Environment.GetEnvironmentVariable("PHOTOFLOW_GPU_RUNTIME") is { Length: > 0 } configured
            ? configured
            : Path.Combine(new PhotoFlow.Core.Paths.DefaultAppPaths().GpuRuntimeDirectory, "cuda13-cudnn9");
}

/// <summary>Parity criteria of spec §14 (decisions A8 and T1 of 2026-10-12).</summary>
public static class ParityTolerances
{
    /// <summary>Box coordinates (normalized), after query matching.</summary>
    public const double Boxes = 5e-4;

    /// <summary>Class scores (sigmoid of logits), after query matching.</summary>
    public const double Scores = 5e-3;
    public const float MinIou = 0.95f;
    public const float MaxScoreDiff = 0.02f;
    public const float ThresholdBand = 0.05f;
}

/// <summary>Greedy per-class matching of detections above the threshold (spec §14).</summary>
public static class DetectionMatcher
{
    public sealed record Result(int Matched, int ToleratedUnmatched, double MinIou, double MaxScoreDiff, IReadOnlyList<string> Failures);

    public static Result Compare(IReadOnlyList<Detection> expected, IReadOnlyList<Detection> actual, float threshold)
    {
        var failures = new List<string>();
        int matched = 0, tolerated = 0;
        double minIou = 1, maxScoreDiff = 0;

        foreach (var classId in expected.Select(d => d.ClassId).Union(actual.Select(d => d.ClassId)))
        {
            var remaining = actual.Where(d => d.ClassId == classId).ToList();
            foreach (var reference in expected.Where(d => d.ClassId == classId).OrderByDescending(d => d.Score))
            {
                var best = remaining.OrderByDescending(d => d.Box.IntersectionOverUnion(reference.Box)).FirstOrDefault();
                var iou = best is null ? 0 : best.Box.IntersectionOverUnion(reference.Box);
                if (best is not null && iou >= ParityTolerances.MinIou)
                {
                    remaining.Remove(best);
                    matched++;
                    var scoreDiff = Math.Abs(best.Score - reference.Score);
                    minIou = Math.Min(minIou, iou);
                    maxScoreDiff = Math.Max(maxScoreDiff, scoreDiff);
                    if (scoreDiff > ParityTolerances.MaxScoreDiff)
                    {
                        failures.Add($"{reference.Label} score {reference.Score:F4} vs {best.Score:F4}");
                    }
                }
                else if (IsBorderline(reference.Score, threshold))
                {
                    tolerated++;
                }
                else
                {
                    failures.Add($"missing {reference.Label} ({reference.Score:F3}), best IoU {iou:F3}");
                }
            }

            foreach (var extra in remaining)
            {
                if (IsBorderline(extra.Score, threshold))
                {
                    tolerated++;
                }
                else
                {
                    failures.Add($"unexpected {extra.Label} ({extra.Score:F3})");
                }
            }
        }

        return new Result(matched, tolerated, minIou, maxScoreDiff, failures);
    }

    private static bool IsBorderline(float score, float threshold) => Math.Abs(score - threshold) <= ParityTolerances.ThresholdBand;
}
