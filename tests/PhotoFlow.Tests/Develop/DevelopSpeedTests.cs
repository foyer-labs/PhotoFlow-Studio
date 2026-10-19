// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PhotoFlow.App.Services.Develop;
using PhotoFlow.App.ViewModels;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Paths;
using PhotoFlow.Tests.Docs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Develop;

/// <summary>Develop with many photos (M7, M8): groups in the same light, previews prepared in the background.</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class DevelopSpeedUnitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 20, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Exposure_value_at_iso_100()
    {
        Assert.Equal(15, DevelopGroups.Ev100(new ExifSummary(null, null, 100, 1 / 125.0, 16, null, null, null))!.Value, 0.05); // sunny 16
        Assert.Null(DevelopGroups.Ev100(new ExifSummary(null, null, null, 1 / 125.0, 16, null, null, null)));
    }

    [Fact]
    public void A_pause_or_a_change_of_light_starts_a_new_group()
    {
        var groups = DevelopGroups.Assign([
            (T0, 10.0), (T0.AddMinutes(1), 10.3), (T0.AddMinutes(2), 10.1), // the same room
            (T0.AddMinutes(3), 13.0),                                         // outside: 3 stops more
            (T0.AddMinutes(4), 13.2),
            (T0.AddMinutes(40), 13.1),                                        // after a pause
            (null, null),
        ]);
        Assert.Equal([0, 0, 0, 1, 1, 2, 2], groups);
    }

    [Fact]
    public void The_preview_key_ignores_order_and_number_formatting()
    {
        var a = JsonNode.Parse("""{"tone":{"contrast":10,"exposure_ev":0.0},"denoise":{"luma_strength":1}}""")!.AsObject();
        var b = JsonNode.Parse("""{"denoise":{"luma_strength":1.0},"tone":{"exposure_ev":0,"contrast":10.0}}""")!.AsObject();
        Assert.Equal(PreviewCache.Key(1, a, 2560), PreviewCache.Key(1, b, 2560));
        Assert.NotEqual(PreviewCache.Key(1, a, 2560), PreviewCache.Key(2, a, 2560));
        b["tone"]!["contrast"] = 11;
        Assert.NotEqual(PreviewCache.Key(1, a, 2560), PreviewCache.Key(1, b, 2560));
    }

    [Fact]
    public void A_stored_preview_comes_back_with_its_frame()
    {
        using var temp = new TempFolder("pf-preview-cache");
        var paths = new DefaultAppPaths(temp.Path);
        Directory.CreateDirectory(paths.TempDirectory);
        string Png(string name)
        {
            var file = Path.Combine(paths.TempDirectory, name);
            File.WriteAllBytes(file, [137, 80, 78, 71]);
            return file;
        }

        var cache = new PreviewCache(paths);
        var render = new global::PhotoFlow.Core.Develop.DevelopRender(Png("a-prima.png"), Png("a-dopo.png"), null, 200, 100, (0, 0, 4000, 2000), 42, (4000, 2000));
        var stored = cache.Store("1-abc", render);
        Assert.NotNull(stored);
        Assert.False(File.Exists(render.BeforeFile)); // moved into the cache
        var again = cache.TryGet("1-abc")!;
        Assert.Equal((4000, 2000), again.Frame);
        Assert.Equal(42, again.Reduction);
        Assert.True(File.Exists(again.AfterFile));
        Assert.Null(cache.TryGet("2-abc"));
    }
}

/// <summary>The real engine on the demo catalog: the next photo is prepared while the first is open, and opens from the cache.</summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class DevelopSpeedTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-develop-speed");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task The_next_photo_is_prepared_and_opens_from_the_cache()
    {
        Assert.SkipUnless(DevelopEngineClient.IsAvailable, "Motore di sviluppo non disponibile (engine/.venv)");
        var (host, byFile, _) = await DocsScreenshots.PrepareAsync(_temp.Path, Ct);
        await using var _ = host;
        var ids = new[] { byFile["DSC_0401.jpg"], byFile["DSC_0402.jpg"], byFile["DSC_0420.jpg"] };
        host.WorkingSet.Set("Prova", ids, ids[0], "Mare");
        using var develop = new DevelopViewModel(host, new NullNavigator());
        await develop.ActivateAsync();
        await Wait(() => develop.HasPhoto && develop.After is not null && !develop.IsComputing, 120);
        await Wait(() => develop.Photos[1].IsPrepared, 120);

        // The second photo was never developed: prepared with the automatic preset, it opens with the same key.
        develop.StepCommand.Execute("1");
        await Wait(() => develop.HasPhoto && develop.Active == develop.Photos[1] && develop.After is not null, 30);
        Assert.False(develop.IsComputing); // nothing computed: it came from the cache
        Assert.Equal([0, 0, 1], develop.Photos.Select(p => p.GroupIndex)); // the beach in a row, the sailing boat the day after
    }

    private static async Task Wait(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
    }

    public void Dispose() => _temp.Dispose();

    private sealed class NullNavigator : IShellNavigator
    {
        public void ShowSection(string sectionId)
        {
        }

        public void ShowPage(PageViewModel page)
        {
        }

        public void OpenReview(long jobId)
        {
        }
    }
}
