// SPDX-License-Identifier: Apache-2.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoFlow.App.Services.Develop;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.App.Views.Develop;
using PhotoFlow.Tests.Docs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Develop;

/// <summary>The zoom of Develop (decision S25): the levels of the wheel and the view kept inside the photo.</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class DevelopZoomUnitTests
{
    [Fact]
    public void The_wheel_goes_from_fit_to_800_percent_and_back()
    {
        const double fit = 0.3;
        Assert.Equal(0.5, DevelopViewer.StepZoom(fit, fit, 1));      // from fit, the first level above it
        Assert.Equal(0.75, DevelopViewer.StepZoom(0.5, fit, 1));
        Assert.Equal(1, DevelopViewer.StepZoom(0.75, fit, 1));
        Assert.Equal(8, DevelopViewer.StepZoom(8, fit, 1));          // no further than 800%
        Assert.Equal(0.5, DevelopViewer.StepZoom(0.75, fit, -1));
        Assert.Equal(0, DevelopViewer.StepZoom(0.5, fit, -1));       // below the fitted size: fit
        Assert.Equal(0, DevelopViewer.StepZoom(0.31, fit, -1));
    }

    [Fact]
    public void The_view_stays_inside_the_photo()
    {
        var viewport = new PixelSize(1000, 500);
        var frame = new Size(4000, 1000);
        Assert.Equal(0.25, DevelopViewer.FitZoom(viewport, frame));
        // At 100% the view is 1000 × 500 photo pixels: the centre cannot go nearer than half of it to the edges.
        Assert.Equal(new Point(500, 250), DevelopViewer.ClampCentre(new Point(0, 0), 1, viewport, frame));
        Assert.Equal(new Point(3500, 750), DevelopViewer.ClampCentre(new Point(9000, 9000), 1, viewport, frame));
        // At 40% the height of the photo fits: centred vertically, free horizontally.
        Assert.Equal(new Point(2000, 500), DevelopViewer.ClampCentre(new Point(2000, 100), 0.4, viewport, frame));
        Assert.Equal(new Point(2000, 500), DevelopViewer.ClampCentre(new Point(10, 10), 0, viewport, frame));
    }
}

/// <summary>The real engine on the demo catalog: the wheel on the photo zooms on the pointer, the slider goes back to fit.</summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class DevelopZoomTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-develop-zoom");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task The_wheel_zooms_on_the_pointer_and_the_slider_goes_back_to_fit()
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
        var frame = develop.FrameSize;
        Assert.Equal(0, develop.Zoom);
        Assert.Equal(0, develop.ZoomSlider);
        Assert.StartsWith("Adatta", develop.ZoomText, StringComparison.Ordinal);

        // One notch on the left part of the photo: the next level, and the view moves towards the pointer.
        var viewer = window.GetVisualDescendants().OfType<DevelopViewer>().Single();
        var origin = viewer.TranslatePoint(default, window)!.Value;
        var pointer = new Point(origin.X + viewer.Bounds.Width * 0.2, origin.Y + viewer.Bounds.Height / 2);
        window.MouseWheel(pointer, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.True(develop.Zoom > develop.FitZoom, $"zoom {develop.Zoom}, fit {develop.FitZoom}");
        Assert.True(develop.Centre.X < frame.Width / 2, $"centre {develop.Centre}");
        Assert.True(develop.ZoomSlider > 0);

        // More notches, up to 150%: the engine computes the visible part at full resolution.
        while (develop.Zoom < 1.5)
        {
            window.MouseWheel(pointer, new Vector(0, 1));
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(1.5, develop.Zoom);
        Assert.Equal("150%", develop.ZoomText);
        await Wait(() => develop.Region.Width < frame.Width && !develop.IsComputing && develop.After is not null, 60);
        Assert.Equal(develop.Viewport.Width / 1.5, develop.Region.Width, 2.0);

        // Ctrl −, then the slider all the way down: the whole photo again, without computing it.
        develop.ZoomStepCommand.Execute("-1");
        Assert.Equal(1, develop.Zoom);
        develop.ZoomSlider = 0;
        Assert.Equal(0, develop.Zoom);
        await Wait(() => develop.Region.Width >= frame.Width - 1, 30);
        Assert.False(develop.IsComputing);
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
