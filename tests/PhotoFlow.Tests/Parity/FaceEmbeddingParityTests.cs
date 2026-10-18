// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Faces.Recognition;
using PhotoFlow.Inference;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Parity;

/// <summary>
/// Face fingerprints against insightface 2.1 (ArcFace) and OpenCV 5.0 (FaceRecognizerSF): alignment matrix,
/// warpAffine port, aligned crops and embeddings (references from tools/lab/face_embed_refs.py); AdaFace IR-101 (P22,
/// ONNX exported on the PC) against the authors' PyTorch network on the same crops (tools/lab/adaface_refs.py).
/// </summary>
[Trait(TestCategory.Name, TestCategory.Parity)]
public sealed class FaceEmbeddingParityTests
{
    /// <summary>A pixel may differ by one level where OpenCV's float rounding differs; never more.</summary>
    public const int MaxPixelDifference = 1;
    public const double MaxDifferingPixels = 0.01;
    public const double MinCosine = 0.999;

    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Warp_affine_matches_opencv()
    {
        var (folder, index) = References();
        var failures = new List<string>();
        var worst = 0.0;
        var cases = index["warps"]!.AsArray();
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i]!;
            var source = new RgbImage(c["width"]!.GetValue<int>(), c["height"]!.GetValue<int>(), File.ReadAllBytes(Path.Combine(folder, $"warp_{i}.src.rgb")));
            var matrix = c["matrix"]!.AsArray().SelectMany(r => r!.AsArray().Select(v => v!.GetValue<double>())).ToArray();
            var (maxDiff, fraction) = Compare(AffineWarp.Bilinear(source, matrix, 112, 112), File.ReadAllBytes(Path.Combine(folder, $"warp_{i}.dst.rgb")));
            worst = Math.Max(worst, fraction);
            if (maxDiff > MaxPixelDifference || fraction > MaxDifferingPixels)
            {
                failures.Add($"warp {i}: max Δ {maxDiff}, {fraction:P2} differing");
            }
        }

        Report("warp", new { worstDifferingFraction = worst, failures });
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("arcface", ExecutionTarget.Cpu)]
    [InlineData("arcface", ExecutionTarget.Gpu)]
    [InlineData("sface", ExecutionTarget.Cpu)]
    [InlineData("adaface", ExecutionTarget.Cpu)]
    [InlineData("adaface", ExecutionTarget.Gpu)]
    public async Task Crops_and_embeddings_match_the_references(string model, ExecutionTarget target)
    {
        var (folder, index) = References();
        if (model == "adaface" && !File.Exists(Path.Combine(folder, "adaface_index.json")))
        {
            Assert.Skip($"References missing: run tools/lab/adaface_refs.py ({folder}).");
        }

        var faces = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "index.json")))!["photos"]!.AsArray();
        var provider = new OnnxRuntimeInferenceProvider(new InferenceOptions(GpuRuntime.Directory));
        if (target == ExecutionTarget.Gpu && !provider.Detected.AvailableBackends.Contains(ExecutionBackend.Cuda))
        {
            Assert.Skip($"CUDA not available: {provider.GpuError}");
        }

        var (id, file) = model switch
        {
            "arcface" => ("insightface-buffalo-l", ArcFaceEmbedder.File),
            "adaface" => ("adaface-ir101-webface12m", AdaFaceEmbedder.File),
            _ => ("opencv-sface-2021dec", SFaceEmbedder.File),
        };
        using var session = await provider.CreateSessionAsync(
            new ModelDescriptor(id, ModelFunction.Faces, model, "test", Path.Combine(TestData.RequireDirectory("models", id), file), string.Empty, model), target, Ct);
        IFaceEmbedder embedder = model switch
        {
            "arcface" => new ArcFaceEmbedder(session),
            "adaface" => new AdaFaceEmbedder(session),
            _ => new SFaceEmbedder(session),
        };
        var (cropName, embeddingName) = model switch
        {
            "arcface" => ("arc_crop", "arc_emb"),
            "adaface" => ("arc_crop", "ada_emb"), // the same alignment as ArcFace
            _ => ("sface_crop", "sface_emb"),
        };
        var failures = new List<string>();
        double minCosine = 1, worstCrop = 0, maxMatrix = 0;
        var images = new Dictionary<string, RgbImage>();
        foreach (var entry in index["faces"]!.AsArray().Select(f => f!))
        {
            var stem = entry["image"]!.GetValue<string>();
            var k = entry["face"]!.GetValue<int>();
            var photo = faces.Single(p => p!["image"]!.GetValue<string>() == stem)!;
            if (!images.TryGetValue(stem, out var image))
            {
                image = new RgbImage(photo["width"]!.GetValue<int>(), photo["height"]!.GetValue<int>(), File.ReadAllBytes(Path.Combine(folder, stem + ".rgb")));
                images[stem] = image;
            }

            var face = photo["faces"]!.AsArray()[k]!;
            var kps = face["kps"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
            var keypoints = Enumerable.Range(0, 5).Select(i => new Point2(kps[2 * i], kps[(2 * i) + 1])).ToArray();
            if (model == "arcface")
            {
                var expected = entry["arc_matrix"]!.AsArray().SelectMany(r => r!.AsArray().Select(v => v!.GetValue<double>())).ToArray();
                var actual = FaceAlignment.EstimateNorm(keypoints);
                maxMatrix = Math.Max(maxMatrix, expected.Zip(actual).Max(p => Math.Abs(p.First - p.Second)));
            }

            var crop = FaceAlignment.AlignedCrop(image, keypoints);
            var (maxDiff, fraction) = Compare(crop, File.ReadAllBytes(Path.Combine(folder, $"{stem}.f{k}.{cropName}.rgb")));
            worstCrop = Math.Max(worstCrop, fraction);
            var detected = new DetectedFace(new PixelBox(0, 0, 1, 1), 1, keypoints, null, null, null);
            var reference = Vectors.Normalize(ReadFloats(folder, $"{stem}.f{k}.{embeddingName}"));
            var cosine = Vectors.Cosine(await embedder.EmbedAsync(image, detected, Ct), reference);
            minCosine = Math.Min(minCosine, cosine);
            if (cosine < MinCosine || maxDiff > 2 * MaxPixelDifference)
            {
                failures.Add($"{stem} face {k}: cosine {cosine:F6}, crop max Δ {maxDiff} ({fraction:P2})");
            }
        }

        Report($"face-embedding-{model}-{target}", new { minCosine, worstCropDifferingFraction = worstCrop, maxMatrixDifference = maxMatrix, failures });
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static (int MaxDiff, double Fraction) Compare(RgbImage actual, byte[] expected)
    {
        var a = actual.Pixels.Span;
        var max = 0;
        var differing = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            var d = Math.Abs(a[i] - expected[i]);
            max = Math.Max(max, d);
            differing += d > 0 ? 1 : 0;
        }

        return (max, (double)differing / expected.Length);
    }

    private static (string Folder, JsonNode Index) References()
    {
        var folder = TestData.RequireDirectory("parity", "faces");
        var path = Path.Combine(folder, "embed_index.json");
        if (!File.Exists(path))
        {
            Assert.Skip($"References missing: run tools/lab/face_embed_refs.py ({path}).");
        }

        return (folder, JsonNode.Parse(File.ReadAllText(path))!);
    }

    private static void Report(string name, object content)
    {
        var folder = TestData.RequireDirectory("parity", "reports");
        File.WriteAllText(Path.Combine(folder, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.json"), JsonSerializer.Serialize(content, ReportJson));
        TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(content, ReportJson));
    }

    private static float[] ReadFloats(string folder, string name) =>
        MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(folder, name + ".bin"))).ToArray();
}
