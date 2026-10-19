// SPDX-License-Identifier: Apache-2.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoFlow.App.Services.Develop;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Tests.Docs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Develop;

/// <summary>The panel of Develop scrolls with the mouse wheel wherever the pointer is, sliders included.</summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class DevelopPanelTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-develop-panel");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task The_panel_scrolls_with_the_wheel()
    {
        Assert.SkipUnless(DevelopEngineClient.IsAvailable, "Motore di sviluppo non disponibile (engine/.venv)");
        var (host, byFile, _) = await DocsScreenshots.PrepareAsync(_temp.Path, Ct);
        await using var _ = host;
        host.WorkingSet.Set("Prova", [byFile["DSC_0401.jpg"]], null, "Mare");
        using var shell = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        shell.ShowSection(SectionCatalog.Develop);
        var develop = Assert.IsType<DevelopViewModel>(shell.CurrentPage);
        await Wait(() => develop.HasPhoto && develop.After is not null && !develop.IsComputing, 120);

        var panel = window.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible).MaxBy(s => s.TranslatePoint(default, window)?.X ?? 0)!;
        Assert.True(panel.Extent.Height > panel.Viewport.Height + 100, $"extent {panel.Extent.Height} viewport {panel.Viewport.Height}");
        var origin = panel.TranslatePoint(default, window)!.Value;

        // On a slider of the panel and on its free space.
        foreach (var x in new[] { origin.X + 170, origin.X + 20 })
        {
            panel.Offset = default;
            Dispatcher.UIThread.RunJobs();
            window.MouseWheel(new Point(x, origin.Y + 500), new Vector(0, -3));
            Dispatcher.UIThread.RunJobs();
            Assert.True(panel.Offset.Y > 0, $"no scroll at x={x}");
        }
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
}
