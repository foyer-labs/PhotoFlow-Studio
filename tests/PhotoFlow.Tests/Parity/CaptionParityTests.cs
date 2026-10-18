// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Imaging;
using PhotoFlow.Inference;
using PhotoFlow.Tagging.Captions;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Parity;

/// <summary>
/// Florence-2 captions (decision F5-1) against the original PyTorch model (tools/lab/florence_refs.py, transformers):
/// the image input within 1e-5, then the generated token ids and the caption identical, on the CPU and on the GPU.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Parity)]
public sealed class CaptionParityTests
{
    public const double MaxInputDifference = 1e-5;

    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ExecutionTarget.Cpu)]
    [InlineData(ExecutionTarget.Gpu)]
    public async Task Captions_match_the_original_model(ExecutionTarget target)
    {
        var folder = TestData.RequireDirectory("parity", "florence");
        var indexPath = Path.Combine(folder, "index.json");
        if (!File.Exists(indexPath))
        {
            Assert.Skip($"Parity references missing: run tools/lab/florence_refs.py ({indexPath}).");
        }

        var models = TestData.RequireDirectory("models", "florence2-base-ft");
        var provider = new OnnxRuntimeInferenceProvider(new InferenceOptions(GpuRuntime.Directory));
        if (target == ExecutionTarget.Gpu && !provider.Detected.AvailableBackends.Contains(ExecutionBackend.Cuda))
        {
            Assert.Skip("CUDA not available.");
        }

        async Task<IInferenceSession> Open(string file) =>
            await provider.CreateSessionAsync(new ModelDescriptor("florence2-base-ft", ModelFunction.Caption, "Florence-2", "lab", Path.Combine(models, file), string.Empty,
                Florence2Captioner.AdapterId), target, Ct);
        using var vision = await Open(Florence2Captioner.VisionFile);
        using var embed = await Open(Florence2Captioner.EmbedFile);
        using var encoder = await Open(Florence2Captioner.EncoderFile);
        using var decoder = await Open(Florence2Captioner.DecoderFile);
        var operations = new SkiaImageOperations();
        var tokenizer = ByteLevelDecoder.Load(Path.Combine(models, Florence2Captioner.TokenizerFile));
        var captioner = new Florence2Captioner(vision, embed, encoder, decoder, tokenizer, operations);

        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, Ct))!;
        Assert.Equal(index["prompt_ids"]!.AsArray().Select(n => n!.GetValue<long>()).Where(id => id < 51289), Florence2Captioner.DetailedCaptionPrompt);
        double maxInput = 0;
        var identical = 0;
        var failures = new List<string>();
        var clock = new Stopwatch();
        var images = index["images"]!.AsArray().Select(e => e!).ToList();
        foreach (var entry in images)
        {
            var stem = entry["stem"]!.GetValue<string>();
            var image = new RgbImage(entry["width"]!.GetValue<int>(), entry["height"]!.GetValue<int>(), await File.ReadAllBytesAsync(Path.Combine(folder, stem + ".rgb"), Ct));
            var pixels = MemoryMarshal.Cast<byte, float>(await File.ReadAllBytesAsync(Path.Combine(folder, stem + ".pixels.bin"), Ct));
            var input = Florence2Captioner.PrepareInput(image, operations).AsFloat32().Span;
            for (var i = 0; i < input.Length; i++)
            {
                maxInput = Math.Max(maxInput, Math.Abs(input[i] - pixels[i]));
            }

            clock.Start();
            var ids = await captioner.GenerateAsync(image, Ct);
            clock.Stop();
            var expected = entry["ids"]!.AsArray().Select(n => n!.GetValue<long>()).ToArray();
            var caption = tokenizer.Decode(ids).Trim();
            var expectedCaption = entry["caption"]!.GetValue<string>();
            if (ids.SequenceEqual(expected) && caption == expectedCaption)
            {
                identical++;
            }
            else
            {
                failures.Add($"{stem}: '{caption}' expected '{expectedCaption}'");
            }
        }

        var report = JsonSerializer.Serialize(new { target = target.ToString(), maxInput, images = images.Count, identical, msPerCaption = clock.Elapsed.TotalMilliseconds / images.Count, failures }, ReportJson);
        await File.WriteAllTextAsync(Path.Combine(TestData.RequireDirectory("parity", "reports"), $"florence-{target}-{DateTime.Now:yyyyMMdd-HHmmss}.json"), report, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        Assert.True(maxInput <= MaxInputDifference, $"input Δ {maxInput}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
