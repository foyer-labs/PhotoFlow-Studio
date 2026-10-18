// SPDX-License-Identifier: Apache-2.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>Duplicates (4f, 4g) with the keyboard: 2 chooses the copy to keep, Enter moves the other into _duplicati, I ignores a group, then "no duplicates".</summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class DuplicatesKeyboardTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-duplicates-keys");

    [AvaloniaFact]
    public async Task A_copy_is_kept_the_other_moved_and_a_group_ignored()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        await using var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None);
        await host.UpdateSettingsAsync(s => s with { SetupCompleted = true }, CancellationToken.None);
        var ct = CancellationToken.None;
        var root = _temp.Sub("library");
        var library = await host.Catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), ct);
        foreach (var name in new[] { "a", "b" })
        {
            var asset = await host.Catalog.Assets.AddAsync(new Asset(0, $"q{name}", $"h{name}", "JPG", "jpeg", 300, 200, null, DateSource.Unknown, null, AssetState.Active), ct);
            foreach (var folder in new[] { "2023", "copia" })
            {
                Directory.CreateDirectory(Path.Combine(root, folder));
                SyntheticImages.WriteScene(Path.Combine(root, folder, name + ".jpg"), 300, 200, name[0]);
                await host.Catalog.Assets.AddLocationAsync(new AssetLocation(0, asset, library, Path.Combine(folder, name + ".jpg"), DateTimeOffset.UtcNow), ct);
            }
        }

        using var shell = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        shell.ShowSection(SectionCatalog.Duplicates);
        var duplicates = Assert.IsType<DuplicatesViewModel>(shell.CurrentPage);
        await WaitAsync(() => duplicates.ExactGroups.Count == 2 && duplicates.Copies.Count == 2);
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendantsOf<DuplicatesView>()?.Focus();

        // Keep the second copy ("copia/a.jpg"), Enter moves "2023/a.jpg" into _duplicati.
        Assert.Equal("a.jpg", duplicates.Copies[0].Name);
        Press(window, PhysicalKey.Digit2);
        Assert.True(duplicates.Copies[1].IsKept);
        Assert.False(duplicates.Copies[0].IsKept);

        // The mouse does the same: a click on a copy keeps it.
        var tiles = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("copy")).ToList();
        Assert.Equal(2, tiles.Count);
        Click(window, tiles[0]);
        Assert.True(duplicates.Copies[0].IsKept);
        Assert.False(duplicates.Copies[1].IsKept);
        Click(window, tiles[1]);
        Assert.True(duplicates.Copies[1].IsKept);
        window.GetVisualDescendantsOf<DuplicatesView>()?.Focus();
        Press(window, PhysicalKey.Enter);
        await WaitAsync(() => duplicates.ExactGroups.Count == 1);
        Assert.True(File.Exists(Path.Combine(root, "_duplicati", "2023", "a.jpg")));
        Assert.True(File.Exists(Path.Combine(root, "copia", "a.jpg")));

        // I: the other group is not a duplicate; nothing is left.
        Press(window, PhysicalKey.I);
        await WaitAsync(() => duplicates.IsEmpty);
        Assert.True(File.Exists(Path.Combine(root, "2023", "b.jpg")));
        Assert.True(File.Exists(Path.Combine(root, "copia", "b.jpg")));

        // F5: the ignored group stays out, the resolved one is gone (its copy is in _duplicati).
        Press(window, PhysicalKey.F5);
        await WaitAsync(() => !duplicates.IsScanning && duplicates.IsEmpty);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A left click in the middle of <paramref name="target"/>, as the window receives it.</summary>
    private static void Click(Window window, Control target)
    {
        Dispatcher.UIThread.RunJobs();
        var center = target.TranslatePoint(new Avalonia.Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
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
