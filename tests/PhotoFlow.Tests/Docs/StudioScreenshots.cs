// SPDX-License-Identifier: Apache-2.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PhotoFlow.App.Services.Develop;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Docs;

/// <summary>
/// Screenshots of the PhotoFlow Studio workflow (docs/studio/flusso.md): from the review of a job to the development
/// with the real engine and the export, on the demo catalog of drawn pictures (no photo, no real face). Runs only when
/// <c>PHOTOFLOW_STUDIO_SCREENSHOTS</c> names the output folder and the engine is available.
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class StudioScreenshots : IDisposable
{
    private readonly TempFolder _temp = new("pf-studio-docs");

    [AvaloniaFact]
    public async Task Screens_of_the_workflow()
    {
        if (Environment.GetEnvironmentVariable("PHOTOFLOW_STUDIO_SCREENSHOTS") is not { Length: > 0 } output)
        {
            Assert.Skip("Set PHOTOFLOW_STUDIO_SCREENSHOTS to the output folder to make the screenshots of the workflow.");
            return;
        }

        Directory.CreateDirectory(output);
        var drive = DocsScreenshots.MountDemoDrive(_temp.Path);
        try
        {
            await ShootAsync(drive, output, CancellationToken.None);
        }
        finally
        {
            if (drive != _temp.Path)
            {
                DocsScreenshots.RunSubst($"{drive[..2]} /d");
            }
        }
    }

    private static async Task ShootAsync(string drive, string output, CancellationToken ct)
    {
        var (host, _, _) = await DocsScreenshots.PrepareAsync(drive, ct);
        await using var _ = host;
        using var shell = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        Task Shot(string name) => DocsScreenshots.ShotAsync(window, output, name);
        static Task Wait(Func<bool> condition, int seconds) => DocsScreenshots.WaitAsync(() => Task.FromResult(condition()), seconds);

        // 1. Home: where the work starts.
        shell.ShowSection(SectionCatalog.Home);
        shell.RefreshStatus();
        await Shot("s01-home");

        // 2. Review: choose before developing.
        shell.ShowSection(SectionCatalog.Review);
        var review = Assert.IsType<ReviewViewModel>(shell.CurrentPage);
        await Wait(() => review.Rows.Count > 0 && review.HasBest && review.JobTitle.Length > 0, 30);
        await Shot("s02-revisione");

        // 3. Develop: the best photos of the review, with the automatic noise reduction.
        await review.DevelopBestCommand.ExecuteAsync(null);
        var develop = Assert.IsType<DevelopViewModel>(shell.CurrentPage);
        await Wait(() => develop.HasPhoto && develop.After is not null && develop.Histogram is not null && !develop.IsComputing && !develop.IsBusy
                         && develop.Photos.All(p => p.ThumbnailPath is null || p.Thumbnail is not null), 180);
        await Shot("s03-sviluppo");

        // 4. A few touches, then the selection of the strip for «apply to the selected».
        develop.Exposure = 0.3;
        develop.Saturation = 20;
        develop.Contrast = 15;
        await Task.Delay(600);
        await Wait(() => develop.After is not null && !develop.IsComputing && !develop.IsBusy, 180);
        foreach (var item in develop.Photos.Skip(1).Take(3))
        {
            develop.Click(item, add: true);
        }

        await Shot("s04-sviluppo-modifiche");

        // The lower blocks of the panel: tones, noise (on with the automatic preset), advanced.
        var panel = window.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible).MaxBy(s => s.TranslatePoint(default, window)?.X ?? 0)!;
        panel.ScrollToEnd();
        await Shot("s05-sviluppo-rumore");
        panel.ScrollToHome();

        // 5. Export: scope, recipe, queue.
        shell.ShowSection(SectionCatalog.Export);
        var export = Assert.IsType<ExportViewModel>(shell.CurrentPage);
        await Wait(() => export.Scopes.Count > 0 && export.Summary.Length > 0, 30);
        export.SelectedRecipe = export.Recipes.First(r => r.Recipe.Id == ExportRecipes.Web);
        await Shot("s06-esporta");

        await export.StartCommand.ExecuteAsync(null);
        await Wait(() => host.Exports.Runs.Count > 0 && host.Exports.Runs.All(r => r.State is ExportState.Completed or ExportState.Failed or ExportState.Cancelled), 600);
        await Shot("s07-esporta-coda");

        // 6. Browse: the archive, from which a search result can go back to Develop.
        shell.ShowSection(SectionCatalog.Browse);
        var browse = Assert.IsType<BrowseViewModel>(shell.CurrentPage);
        await Wait(() => browse.Rows.Sum(r => r.Items.Count) > 10, 30);
        await Shot("s08-consultazione");
    }

    public void Dispose() => _temp.Dispose();
}
