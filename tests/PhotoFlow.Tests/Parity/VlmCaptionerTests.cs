// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services.Vlm;
using PhotoFlow.Core.Paths;
using PhotoFlow.Imaging;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Parity;

/// <summary>
/// Level D on this PC (phase 5b): each model converted by <c>tools/vlm/convert.py</c> in the local test data, run by
/// the real <c>PhotoFlow.Vlm</c> process through <see cref="VlmCaptioner"/>. The caption is in Italian, the same photo
/// gives the same caption (greedy), the temporary JPEGs are removed, and a captioner disposed and made again starts a
/// new process. The approximate parity against PyTorch is <c>tools/vlm/parity.py</c> (D5b-7).
/// </summary>
[Trait(TestCategory.Name, TestCategory.Parity)]
public sealed class VlmCaptionerTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-vlm-captioner");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("qwen3.5-9b")]
    [InlineData("qwen2.5-vl-7b")]
    public async Task The_converted_model_captions_in_Italian(string modelId)
    {
        var model = TestData.RequireDirectory("models", modelId);
        var photo = Path.Combine(TestData.RequireDirectory("lab"), "vlm_test.jpg");
        if (!File.Exists(photo) || !File.Exists(VlmCaptioner.HelperPath))
        {
            Assert.Skip($"Missing {photo} or the helper {VlmCaptioner.HelperPath}.");
        }

        var operations = new SkiaImageOperations();
        await using var stream = File.OpenRead(photo);
        var image = await operations.DecodeAsync(stream, Ct);
        var paths = new DefaultAppPaths(_temp.Sub("profile"));

        string first;
        await using (var captioner = new VlmCaptioner(model, modelId + "@test", GpuRuntime.Directory, operations, paths))
        {
            first = await captioner.CaptionAsync(image, Ct);
            Assert.Equal(first, await captioner.CaptionAsync(image, Ct));
            Assert.Equal("it", captioner.Language);
        }

        Assert.InRange(first.Length, 20, 1000);
        Assert.DoesNotContain("think>", first, StringComparison.Ordinal); // Qwen3.5 answers without reasoning
        Assert.DoesNotContain("<|", first, StringComparison.Ordinal);
        var words = first.ToLowerInvariant().Split([' ', ',', '.', '\''], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        Assert.True(words.Overlaps(["il", "la", "una", "un", "di", "con", "su", "in", "e"]), first);
        Assert.Empty(Directory.Exists(Path.Combine(paths.TempDirectory, "vlm")) ? Directory.GetFiles(Path.Combine(paths.TempDirectory, "vlm")) : []);

        // A new captioner, a new process: the same caption.
        await using var again = new VlmCaptioner(model, modelId + "@test", GpuRuntime.Directory, operations, paths);
        Assert.Equal(first, await again.CaptionAsync(image, Ct));
    }

    public void Dispose() => _temp.Dispose();
}
