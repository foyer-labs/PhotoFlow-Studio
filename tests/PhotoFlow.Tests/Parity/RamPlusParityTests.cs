// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Imaging;
using PhotoFlow.Inference;
using PhotoFlow.Tagging.RamPlus;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Parity;

/// <summary>
/// RAM++ fp32 (decision F3-1) against the references of tools/lab/ramplus_refs.py (the ONNX model on the official
/// transform, itself identical to PyTorch in verify_ramplus.py): preprocessing, probabilities and the tag set at the
/// official thresholds. A tag whose probability lies within <see cref="ThresholdTolerance"/> of its threshold may differ.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Parity)]
public sealed class RamPlusParityTests
{
    public const double MaxInputDifference = 1e-5;
    public const double MaxProbabilityDifference = 2e-3;
    public const double ThresholdTolerance = 2e-3;

    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ExecutionTarget.Cpu)]
    [InlineData(ExecutionTarget.Gpu)]
    public async Task Preprocessing_probabilities_and_tags_match_the_original(ExecutionTarget target)
    {
        var folder = TestData.RequireDirectory("parity", "ramplus");
        var indexPath = Path.Combine(folder, "index.json");
        if (!File.Exists(indexPath))
        {
            Assert.Skip($"Parity references missing: run tools/lab/ramplus_refs.py ({indexPath}).");
        }

        var index = JsonNode.Parse(File.ReadAllText(indexPath))!;
        var models = TestData.RequireDirectory("models", "ramplus-fp32");
        var provider = new OnnxRuntimeInferenceProvider(new InferenceOptions(GpuRuntime.Directory));
        if (target == ExecutionTarget.Gpu && !provider.Detected.AvailableBackends.Contains(ExecutionBackend.Cuda))
        {
            Assert.Skip($"CUDA not available: {provider.GpuError}");
        }

        using var session = await provider.CreateSessionAsync(
            new ModelDescriptor("ramplus-fp32", ModelFunction.Tagger, "RAM++", "fp32", Path.Combine(models, RamPlusTagger.ModelFile), string.Empty, RamPlusTagger.AdapterId),
            target, Ct);
        var classes = RamPlusClasses.Load(Path.Combine(models, RamPlusTagger.TagListFile), Path.Combine(models, RamPlusTagger.ThresholdsFile));
        var operations = new SkiaImageOperations();
        var tagger = new RamPlusTagger(session, operations, classes);
        double maxInput = 0, maxProbability = 0;
        var failures = new List<string>();
        foreach (var photo in index["photos"]!.AsArray().Select(p => p!))
        {
            var stem = photo["stem"]!.GetValue<string>();
            var image = new RgbImage(photo["width"]!.GetValue<int>(), photo["height"]!.GetValue<int>(), File.ReadAllBytes(Path.Combine(folder, stem + ".rgb")));
            var expectedInput = ReadFloats(folder, stem + ".ram_input");
            var input = RamPlusTagger.PrepareInput(image, operations).AsFloat32().Span;
            var inputDifference = 0.0;
            for (var i = 0; i < expectedInput.Length; i++)
            {
                inputDifference = Math.Max(inputDifference, Math.Abs(input[i] - expectedInput[i]));
            }

            var expected = ReadFloats(folder, stem + ".ram_ref");
            var probabilities = await tagger.ProbabilitiesAsync(image, Ct);
            var probabilityDifference = 0.0;
            var tagDifferences = new List<string>();
            for (var i = 0; i < expected.Length; i++)
            {
                probabilityDifference = Math.Max(probabilityDifference, Math.Abs(probabilities[i] - expected[i]));
                var threshold = classes.Thresholds[i];
                if ((probabilities[i] > threshold) != (expected[i] > threshold) && Math.Abs(expected[i] - threshold) > ThresholdTolerance)
                {
                    tagDifferences.Add(classes.Tags[i]);
                }
            }

            maxInput = Math.Max(maxInput, inputDifference);
            maxProbability = Math.Max(maxProbability, probabilityDifference);
            var tags = (await tagger.TagAsync(image, Ct)).Select(t => t.TagKey).Order(StringComparer.Ordinal).ToList();
            var expectedTags = photo["tags"]!.AsArray().Select(t => t!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
            if (inputDifference > MaxInputDifference || probabilityDifference > MaxProbabilityDifference || tagDifferences.Count > 0)
            {
                failures.Add($"{stem}: input Δ {inputDifference:E2}, probability Δ {probabilityDifference:E2}, tags differing {string.Join(", ", tagDifferences)} (got {string.Join(", ", tags)}; expected {string.Join(", ", expectedTags)})");
            }
        }

        var report = JsonSerializer.Serialize(new { maxInput, maxProbability, failures }, ReportJson);
        await File.WriteAllTextAsync(Path.Combine(TestData.RequireDirectory("parity", "reports"), $"ramplus-{target}-{DateTime.Now:yyyyMMdd-HHmmss}.json"), report, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static float[] ReadFloats(string folder, string name) =>
        MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(folder, name + ".bin"))).ToArray();
}
