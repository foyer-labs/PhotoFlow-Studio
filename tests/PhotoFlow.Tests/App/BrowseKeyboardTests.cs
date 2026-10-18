// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// Consultazione (boards 4d, 4e) with the keyboard: search from the title bar, tokens, navigation, stars, a manual tag
/// added with T and a tag removed (suppressed), "no results" with the way out.
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class BrowseKeyboardTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-browse-keys");

    [AvaloniaFact]
    public async Task Search_navigation_stars_and_tags_with_the_keyboard()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        await using var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None);
        await host.UpdateSettingsAsync(s => s with { SetupCompleted = true }, CancellationToken.None);
        var ct = CancellationToken.None;
        var catalog = host.Catalog;
        var library = await catalog.Libraries.AddAsync(new Library(0, "L", _temp.Sub("lib"), WritePolicy.SidecarOnly, false, LibraryOrigin.Import, null), ct);
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var id = await catalog.Assets.AddAsync(new Asset(0, $"q{i}", null, "JPG", "jpeg", 10, 10, new DateTimeOffset(2025, 8, 10 + i, 12, 0, 0, TimeSpan.Zero), DateSource.Exif, null, AssetState.Active), ct);
            await catalog.Assets.AddLocationAsync(new AssetLocation(0, id, library, i < 3 ? $"2025/Ferragosto/mare_{i}.jpg" : $"2025/Dolomiti/monte_{i}.jpg", DateTimeOffset.UtcNow), ct);
            ids.Add(id);
        }

        await catalog.Tags.ReplaceAutomaticAsync(ids[0], TagSource.RamPlus, "r", [("beach", 0.9), ("sunset", 0.8)], ct);
        await catalog.Tags.ReplaceAutomaticAsync(ids[1], TagSource.RamPlus, "r", [("beach", 0.9)], ct);
        await catalog.Captions.SetAutomaticAsync(ids[1], "en", "A beach with umbrellas.", "florence2-base-ft@1", ct);

        using var shell = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();

        // From the title bar: Ctrl K, type, Enter → Consultazione with the free text.
        Press(window, PhysicalKey.K, RawInputModifiers.Control);
        window.KeyTextInput("ferragosto");
        Press(window, PhysicalKey.Enter);
        var browse = Assert.IsType<BrowseViewModel>(shell.CurrentPage);
        await WaitAsync(() => browse.Rows.Sum(r => r.Items.Count) == 3);
        Assert.Equal("ferragosto", browse.FreeText);
        Focus(window);

        // Navigation: the newest first, → moves.
        Assert.Equal(ids[2], browse.Selected!.AssetId);
        Press(window, PhysicalKey.ArrowRight);
        Assert.Equal(ids[1], browse.Selected!.AssetId);

        // Stars 3.
        Press(window, PhysicalKey.Digit3);
        await WaitAsync(() => browse.DetailRating.StartsWith("★★★", StringComparison.Ordinal));
        Assert.Equal(3, (await catalog.Decisions.GetAsync(ids[1], ct))!.Rating);

        // T: a manual tag typed in the field, Enter applies it.
        Press(window, PhysicalKey.T);
        Assert.True(browse.IsTagging);
        browse.TagText = "vacanze";
        browse.ApplyTagCommand.Execute(null);
        await WaitAsync(() => browse.DetailTags.Any(t => t.Label == "vacanze" && t.IsManual));

        // ✕ on "beach": suppressed, never added again by an analysis.
        var beach = browse.DetailTags.Single(t => t.Label == "beach");
        browse.RemoveTagCommand.Execute(beach);
        await WaitAsync(() => browse.DetailTags.All(t => t.Label != "beach"));
        Assert.Equal(AssetTagState.Suppressed, (await catalog.Tags.GetForAssetAsync(ids[1], ct)).Single(t => t.Tag.Key == "beach").Assignment.State);

        // D: the caption is edited in its field; Enter saves it as the user's, and the keys go back to the page.
        Assert.Equal("A beach with umbrellas.", browse.DetailCaption);
        Press(window, PhysicalKey.D);
        Assert.True(browse.IsEditingCaption);
        Assert.Equal("A beach with umbrellas.", browse.CaptionText);
        browse.CaptionText = "Ferragosto a Rimini, la spiaggia all'alba.";
        Press(window, PhysicalKey.Enter);
        await WaitAsync(() => browse.DetailCaption.StartsWith("Ferragosto a Rimini", StringComparison.Ordinal));
        Assert.Equal(CaptionOrigin.Manual, (await catalog.Captions.GetAsync(ids[1], ct))!.Origin);
        Assert.False(await catalog.Captions.SetAutomaticAsync(ids[1], "en", "A beach.", "florence2-base-ft@2", ct));
        Press(window, PhysicalKey.Digit4);
        await WaitAsync(() => browse.DetailRating.StartsWith("★★★★", StringComparison.Ordinal));

        Save(window, "4d-browse");

        // A token: only the photos with "beach" still active.
        await browse.AddTokenAsync(browse.Suggestions.Single(s => s.Label == "beach"));
        await WaitAsync(() => browse.Rows.Sum(r => r.Items.Count) == 1);

        // No results: the way out removes the rating filter.
        browse.RatingOption = browse.RatingOptions.Single(o => o.Value == 5);
        await WaitAsync(() => browse.IsEmpty && browse.NoResultOptions.Count > 0);
        Save(window, "4e-browse-empty");
        await browse.ApplyNoResultOptionCommand.ExecuteAsync(browse.NoResultOptions.First(o => o.Label.Contains('5', StringComparison.Ordinal)));
        await WaitAsync(() => !browse.IsEmpty);
    }

    public void Dispose() => _temp.Dispose();

    private static void Focus(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendantsOf<BrowseView>()?.Focus();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("PHOTOFLOW_SCREENSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            frame!.Save(Path.Combine(folder, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
