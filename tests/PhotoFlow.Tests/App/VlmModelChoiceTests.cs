// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services;
using PhotoFlow.Core.Inference;
using PhotoFlow.Core.Paths;
using PhotoFlow.Inference.Models;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// The level D model in use (spec §5, U1 revised on 15 Oct 2026): Qwen3.5-9B for a new installation; where Qwen2.5-VL
/// is already converted it stays in use — the captions never change model on their own — until the user chooses
/// Qwen3.5 in Settings › Models.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class VlmModelChoiceTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-vlm-choice");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_level_d_model_changes_only_when_the_user_chooses_it()
    {
        await using (var host = await PhotoFlowHost.StartAsync(new DefaultAppPaths(_temp.Sub("upgraded")), Ct))
        {
            Assert.Null(host.ActiveVlmModelId);
            MarkConverted(host, PhotoFlowHost.PreviousVlmModelId);
            Assert.Equal(PhotoFlowHost.PreviousVlmModelId, host.ActiveVlmModelId);

            MarkConverted(host, PhotoFlowHost.VlmModelId);
            Assert.Equal(PhotoFlowHost.PreviousVlmModelId, host.ActiveVlmModelId); // converted, but not chosen

            await host.ChooseModelAsync(ModelFunction.Vlm, PhotoFlowHost.VlmModelId, Ct);
            Assert.Equal(PhotoFlowHost.VlmModelId, host.ActiveVlmModelId);
        }

        await using (var host = await PhotoFlowHost.StartAsync(new DefaultAppPaths(_temp.Sub("new")), Ct))
        {
            MarkConverted(host, PhotoFlowHost.VlmModelId);
            Assert.Equal(PhotoFlowHost.VlmModelId, host.ActiveVlmModelId);
        }
    }

    public void Dispose() => _temp.Dispose();

    private static void MarkConverted(PhotoFlowHost host, string id)
    {
        var folder = host.Models.InstallDirectory(id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ModelRegistry.ConvertedConfig), "{}");
        File.WriteAllText(Path.Combine(folder, ".installed"), host.Models.Get(id).Version);
    }
}
