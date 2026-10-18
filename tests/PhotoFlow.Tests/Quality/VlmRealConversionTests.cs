// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using PhotoFlow.App.Services;
using PhotoFlow.App.Services.Vlm;
using PhotoFlow.Core.Paths;
using PhotoFlow.Inference.Models;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Quality;

/// <summary>
/// The real conversion of the level D model as the user runs it (phase 5b, U2-U3): the Python of the PC found with the
/// launcher, a new private environment, the pinned packages downloaded (~5 GB), the export (~15 minutes, ~45 GB of
/// space) and the model marked installed; the work folder does not stay. It runs only when
/// <c>PHOTOFLOW_VLM_CONVERT=1</c> and the original weights are in the local test data
/// (<c>models/qwen2.5-vl-7b-source</c>); the profile is a folder of the test data, the weights are only read.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Performance)]
public sealed class VlmRealConversionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_app_converts_the_original_weights()
    {
        if (Environment.GetEnvironmentVariable("PHOTOFLOW_VLM_CONVERT") != "1")
        {
            Assert.Skip("Set PHOTOFLOW_VLM_CONVERT=1 to run the real conversion (downloads ~5 GB, ~15 minutes).");
        }

        var models = TestData.RequireDirectory("models");
        TestData.RequireDirectory("models", PhotoFlowHost.VlmModelId + "-source");
        var paths = new DefaultAppPaths(Path.Combine(TestData.Root!, "vlm-app-profile"), new AppPathOverrides(ModelsDirectory: models));
        using var http = new HttpClient();
        var registry = new ModelRegistry(http, ModelRegistry.EmbeddedManifest(), paths.ModelsDirectory);
        var conversion = new VlmConversion(registry, paths, new ProcessVlmToolchain(), Repository.PathOf("tools", "vlm"));

        var stages = new List<VlmConversionProgress>();
        var clock = Stopwatch.StartNew();
        await conversion.ConvertAsync(PhotoFlowHost.VlmModelId, new Progress<VlmConversionProgress>(stages.Add), Ct);
        TestContext.Current.SendDiagnosticMessage($"Conversion in {clock.Elapsed.TotalMinutes:0.0} min");

        Assert.True(registry.IsInstalled(PhotoFlowHost.VlmModelId));
        Assert.True(File.Exists(Path.Combine(registry.InstallDirectory(PhotoFlowHost.VlmModelId), ModelRegistry.ConvertedConfig)));
        Assert.False(Directory.Exists(Path.Combine(paths.TempDirectory, "vlm-work")));
        Assert.True(conversion.EnvironmentExists);
    }
}
