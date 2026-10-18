// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Imaging;
using PhotoFlow.Inference;
using PhotoFlow.Tagging.Ocr;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Parity;

/// <summary>
/// OCR adapters (decision F4-1) against tools/lab/ocr_refs.py on real photos: detector input and probability map,
/// boxes from the same map identical, recognized text identical with confidence within 1e-3.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Parity)]
public sealed class OcrParityTests
{
    public const double MaxInputDifference = 1e-5;
    public const double MaxProbabilityDifference = 1e-4;
    public const double MaxConfidenceDifference = 1e-3;

    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Detection_boxes_and_recognition_match_the_references()
    {
        var folder = TestData.RequireDirectory("parity", "ocr");
        var indexPath = Path.Combine(folder, "index.json");
        if (!File.Exists(indexPath))
        {
            Assert.Skip($"Parity references missing: run tools/lab/ocr_refs.py ({indexPath}).");
        }

        var models = TestData.RequireDirectory("models", "ppocr");
        var provider = new OnnxRuntimeInferenceProvider(new InferenceOptions(GpuRuntime.Directory));
        using var detector = await provider.CreateSessionAsync(Model(models, PaddleOcr.DetectorFile, PaddleOcr.AdapterId), ExecutionTarget.Cpu, Ct);
        using var recognizer = await provider.CreateSessionAsync(Model(models, PaddleOcr.RecognizerFile, PaddleOcr.AdapterId), ExecutionTarget.Cpu, Ct);
        var operations = new SkiaImageOperations();
        var ocr = new PaddleOcr(detector, recognizer, operations);
        var index = JsonNode.Parse(File.ReadAllText(indexPath))!;
        double maxInput = 0, maxProbability = 0, maxConfidence = 0;
        var texts = 0;
        var failures = new List<string>();
        foreach (var entry in index["images"]!.AsArray().Select(e => e!))
        {
            var stem = entry["stem"]!.GetValue<string>();
            var image = new RgbImage(entry["width"]!.GetValue<int>(), entry["height"]!.GetValue<int>(), File.ReadAllBytes(Path.Combine(folder, stem + ".rgb")));
            var (input, width, height, _) = PaddleOcr.DetectorInput(image, operations);
            Assert.Equal((entry["det_width"]!.GetValue<int>(), entry["det_height"]!.GetValue<int>()), (width, height));
            maxInput = Math.Max(maxInput, MaxDifference(input.AsFloat32().Span, ReadFloats(folder, stem + ".det_input")));
            var expectedProbabilities = ReadFloats(folder, stem + ".det_prob");
            var probabilities = (await detector.RunAsync([input], Ct))[0].AsFloat32().ToArray();
            maxProbability = Math.Max(maxProbability, MaxDifference(probabilities, expectedProbabilities));

            // Boxes from the reference map: the same post-processing gives the same boxes.
            var boxes = PaddleOcr.Boxes(expectedProbabilities, width, height);
            var expectedBoxes = entry["boxes"]!.AsArray().Select(b => b!).ToList();
            if (boxes.Count != expectedBoxes.Count)
            {
                failures.Add($"{stem}: {boxes.Count} boxes, expected {expectedBoxes.Count}");
                continue;
            }

            var resized = operations.Resize(image, width, height, ResampleMode.Bilinear);
            for (var i = 0; i < boxes.Count; i++)
            {
                var expected = expectedBoxes[i];
                var box = boxes[i];
                if ((box.X0, box.Y0, box.X1, box.Y1) != (expected["x0"]!.GetValue<int>(), expected["y0"]!.GetValue<int>(), expected["x1"]!.GetValue<int>(), expected["y1"]!.GetValue<int>()))
                {
                    failures.Add($"{stem} box {i}: {box} expected {expected.ToJsonString()}");
                    continue;
                }

                var crop = Crop(resized, box);
                var (text, confidence) = await ocr.RecognizeAsync(crop, Ct);
                var expectedText = expected["text"]!.GetValue<string>();
                var difference = Math.Abs(confidence - expected["confidence"]!.GetValue<double>());
                maxConfidence = Math.Max(maxConfidence, difference);
                texts++;
                if (text != expectedText || difference > MaxConfidenceDifference)
                {
                    failures.Add($"{stem} box {i}: '{text}' {confidence:F4}, expected '{expectedText}'");
                }
            }
        }

        var report = JsonSerializer.Serialize(new { maxInput, maxProbability, maxConfidence, texts, failures }, ReportJson);
        await File.WriteAllTextAsync(Path.Combine(TestData.RequireDirectory("parity", "reports"), $"ocr-{DateTime.Now:yyyyMMdd-HHmmss}.json"), report, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        Assert.True(maxInput <= MaxInputDifference, $"input Δ {maxInput}");
        Assert.True(maxProbability <= MaxProbabilityDifference, $"probability Δ {maxProbability}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static ModelDescriptor Model(string folder, string file, string adapter) =>
        new("ppocr", ModelFunction.Ocr, "PP-OCR", "lab", Path.Combine(folder, file), string.Empty, adapter);

    private static RgbImage Crop(RgbImage image, TextBox box)
    {
        var (w, h) = (box.X1 - box.X0, box.Y1 - box.Y0);
        var pixels = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            image.Pixels.Span.Slice((((box.Y0 + y) * image.Width) + box.X0) * 3, w * 3).CopyTo(pixels.AsSpan(y * w * 3, w * 3));
        }

        return new RgbImage(w, h, pixels);
    }

    private static double MaxDifference(ReadOnlySpan<float> a, float[] b)
    {
        Assert.Equal(b.Length, a.Length);
        double max = 0;
        for (var i = 0; i < a.Length; i++)
        {
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        }

        return max;
    }

    private static float[] ReadFloats(string folder, string name) =>
        MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(folder, name + ".bin"))).ToArray();
}
