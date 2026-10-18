// SPDX-License-Identifier: Apache-2.0

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// In-app benchmark (board 5f): photos sampled from the libraries (not from the discard folders), stages measured,
/// estimates from the parallel run, result kept in the settings; nothing is written next to the photos.
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class BenchmarkTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-benchmark");

    [AvaloniaFact]
    public async Task The_benchmark_measures_the_stages_and_is_shown_in_the_settings()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        await using var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None);
        var library = _temp.Sub("library");
        Directory.CreateDirectory(Path.Combine(library, "2026"));
        Directory.CreateDirectory(Path.Combine(library, "_scarti"));
        for (var i = 0; i < 12; i++)
        {
            SyntheticImages.WriteJpeg(Path.Combine(library, "2026", $"IMG_{i:0000}.jpg"), 1200, 800);
        }

        SyntheticImages.WriteJpeg(Path.Combine(library, "_scarti", "IMG_9999.jpg"), 1200, 800);
        await host.Catalog.Libraries.AddAsync(new Library(0, "Archivio", library, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), CancellationToken.None);
        var before = Directory.GetFiles(library, "*", SearchOption.AllDirectories).Order().ToList();

        var result = await new BenchmarkRunner(host).RunAsync(10, null, CancellationToken.None);
        Assert.Equal(10, result.Photos);
        Assert.Equal(BenchmarkRunner.StageKeys, result.Stages.Select(s => s.Key));
        Assert.All(result.Stages.Take(2), s => Assert.True(s.MsPerPhoto > 0 && s.PeakRamBytes > 0, s.Key));
        Assert.True(result.ParallelMsPerPhoto > 0);
        Assert.True(result.EstimateArchive(1000) >= result.EstimateImport(1000));
        Assert.Equal(before, Directory.GetFiles(library, "*", SearchOption.AllDirectories).Order().ToList());

        // Kept in the settings and shown by the Hardware tab.
        await host.UpdateSettingsAsync(s => s with { Benchmark = result }, CancellationToken.None);
        var settings = new SettingsViewModel(host);
        var activation = settings.ActivateAsync();
        while (!activation.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        await activation;
        Assert.True(settings.HasBenchmark);
        Assert.Equal(BenchmarkRunner.StageKeys.Length, settings.BenchmarkRows.Count);
        Assert.False(string.IsNullOrEmpty(settings.BenchmarkEstimates));
    }

    public void Dispose() => _temp.Dispose();
}
