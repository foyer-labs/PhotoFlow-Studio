// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Imaging;
using PhotoFlow.Inference;
using PhotoFlow.Tagging.Clip;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Parity;

/// <summary>
/// CLIP ViT-B/32 adapters against the original openai/clip-vit-base-patch32 in PyTorch (references from
/// tools/lab/clip_refs.py): preprocessing, image and text embeddings (cosine ≥ 0.999, spec §14), tokenizer ids.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Parity)]
public sealed class ClipParityTests
{
    public const double MinCosine = 0.999;
    public const double MaxInputDifference = 1e-5;

    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ExecutionTarget.Cpu)]
    [InlineData(ExecutionTarget.Gpu)]
    public async Task Preprocessing_and_image_embeddings_match_the_original(ExecutionTarget target)
    {
        var (folder, index) = References();
        var provider = new OnnxRuntimeInferenceProvider(new InferenceOptions(GpuRuntime.Directory));
        if (target == ExecutionTarget.Gpu && !provider.Detected.AvailableBackends.Contains(ExecutionBackend.Cuda))
        {
            Assert.Skip($"CUDA not available: {provider.GpuError}");
        }

        using var session = await provider.CreateSessionAsync(Model("vision_model.onnx"), target, Ct);
        var operations = new SkiaImageOperations();
        var embedder = new ClipImageEmbedder(session, operations);
        double maxInput = 0, minCosine = 1;
        var failures = new List<string>();
        foreach (var photo in index["photos"]!.AsArray().Select(p => p!))
        {
            var stem = photo["stem"]!.GetValue<string>();
            var image = new RgbImage(photo["width"]!.GetValue<int>(), photo["height"]!.GetValue<int>(), File.ReadAllBytes(Path.Combine(folder, stem + ".rgb")));
            var expectedInput = ReadFloats(folder, stem + ".clip_input");
            var input = ClipImageEmbedder.PrepareInput(image, operations).AsFloat32().Span;
            var inputDifference = 0.0;
            for (var i = 0; i < expectedInput.Length; i++)
            {
                inputDifference = Math.Max(inputDifference, Math.Abs(input[i] - expectedInput[i]));
            }

            maxInput = Math.Max(maxInput, inputDifference);
            var cosine = Vectors.Cosine(await embedder.EmbedAsync(image, Ct), ReadFloats(folder, stem + ".clip_ref"));
            minCosine = Math.Min(minCosine, cosine);
            if (inputDifference > MaxInputDifference || cosine < MinCosine)
            {
                failures.Add($"{stem}: input Δ {inputDifference:E2}, cosine {cosine:F6}");
            }
        }

        await Report($"clip-image-{target}", new { maxInput, minCosine, failures });
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task Tokenizer_ids_and_text_embeddings_match_the_original()
    {
        var (_, index) = References();
        var tokenizer = ClipTokenizer.Load(Path.Combine(TestData.RequireDirectory("models", "clip-vit-b32"), "tokenizer.json"));
        var provider = new OnnxRuntimeInferenceProvider(new InferenceOptions(GpuRuntime.Directory));
        using var session = await provider.CreateSessionAsync(Model("text_model.onnx"), ExecutionTarget.Cpu, Ct);
        var embedder = new ClipTextEmbedder(session, tokenizer);
        var failures = new List<string>();
        double minCosine = 1;
        foreach (var text in index["texts"]!.AsArray().Select(t => t!))
        {
            var prompt = text["prompt"]!.GetValue<string>();
            var expected = text["ids"]!.AsArray().Select(v => v!.GetValue<long>()).ToList();
            expected = expected.Take(expected.IndexOf(ClipTokenizer.EndOfText) + 1).ToList(); // the reference is padded to 77
            var ids = tokenizer.Encode(prompt);
            if (!ids.SequenceEqual(expected))
            {
                failures.Add($"'{prompt}': ids [{string.Join(' ', ids)}] expected [{string.Join(' ', expected)}]");
            }

            var cosine = Vectors.Cosine(await embedder.EmbedAsync(prompt, Ct), text["embedding"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray());
            minCosine = Math.Min(minCosine, cosine);
            if (cosine < MinCosine)
            {
                failures.Add($"'{prompt}': cosine {cosine:F6}");
            }
        }

        await Report("clip-text", new { minCosine, failures });
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static (string Folder, JsonNode Index) References()
    {
        var folder = TestData.RequireDirectory("parity", "clip");
        var indexPath = Path.Combine(folder, "index.json");
        if (!File.Exists(indexPath))
        {
            Assert.Skip($"Parity references missing: run tools/lab/clip_refs.py ({indexPath}).");
        }

        return (folder, JsonNode.Parse(File.ReadAllText(indexPath))!);
    }

    private static ModelDescriptor Model(string file) =>
        new("clip-vit-b32", ModelFunction.Embedding, "CLIP ViT-B/32", "fp32",
            Path.Combine(TestData.RequireDirectory("models", "clip-vit-b32"), file), string.Empty, ClipImageEmbedder.AdapterId);

    private static async Task Report(string name, object content)
    {
        var folder = TestData.RequireDirectory("parity", "reports");
        await File.WriteAllTextAsync(Path.Combine(folder, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.json"), JsonSerializer.Serialize(content, ReportJson), Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(content, ReportJson));
    }

    private static float[] ReadFloats(string folder, string name) =>
        MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(folder, name + ".bin"))).ToArray();
}
