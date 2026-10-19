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
        var (host, byFile, _) = await DocsScreenshots.PrepareAsync(drive, ct);
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

        // M1: the crop on the whole straightened frame, a vertical 4:5 slightly straightened.
        develop.SetCrop(new Avalonia.Rect(0.25, 0, 0.5, 1));
        develop.Aspect = develop.AspectOptions.First(o => o.Key == "4:5");
        develop.Angle = 2.5;
        develop.ToggleCropCommand.Execute(null);
        await Task.Delay(600);
        await Wait(() => develop.After is not null && !develop.IsComputing && !develop.IsBusy, 180);
        await Shot("s09-sviluppo-ritaglio");
        develop.CropDoneCommand.Execute(null);
        await Task.Delay(600);
        await Wait(() => develop.After is not null && !develop.IsComputing && !develop.IsBusy, 180);

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

        // M3, M4: a multiple selection with a colour label; M9: the eyes at 100%.
        shell.ShowSection(SectionCatalog.Review);
        review = Assert.IsType<ReviewViewModel>(shell.CurrentPage);
        await Wait(() => review.Rows.Count > 0 && review.JobTitle.Length > 0, 30);
        var cells = review.Rows.OfType<ReviewRowViewModel>().SelectMany(r => r.Items).ToList();
        review.SelectCommand.Execute(cells[3]);
        review.ToggleMark(cells[4]);
        review.ToggleMark(cells[5]);
        await review.SetLabelCommand.ExecuteAsync("Green");
        await Shot("s10-revisione-selezione");
        review.ClearMarksCommand.Execute(null);
        review.SelectCommand.Execute(cells.First(c => c.AnalyzedAssetId == byFile[DemoCatalog.GroupPhoto]));
        review.ToggleLargePhotoCommand.Execute(null);
        await Wait(() => review.LargeImage is { PixelSize.Width: > 1000 }, 30);
        await review.ToggleLoupeCommand.ExecuteAsync(null);
        await Shot("s11-revisione-lente");
        await review.ToggleLoupeCommand.ExecuteAsync(null);
        review.ToggleLargePhotoCommand.Execute(null);

        // M5, M2: a card of two bodies, the clock of the second one 5 minutes behind; author and copyright of the event.
        var card = Path.Combine(drive, "Scheda SD", "DCIM", "100NIKON");
        Directory.CreateDirectory(card);
        await using (var exif = Support.ExifToolFixture.Start())
        {
            for (var i = 0; i < 8; i++)
            {
                var file = Path.Combine(card, $"DSC_{2000 + i:0000}.jpg");
                DemoScenes.Draw(i % 2 == 0 ? DemoScene.Portrait : DemoScene.Group, 30 + i, file);
                await exif.ExecuteAsync(["-overwrite_original", $"-DateTimeOriginal={new DateTime(2026, 6, 20, 15, 0, 0).AddMinutes(i * 3):yyyy:MM:dd HH:mm:ss}", "-Make=Nikon",
                    i < 5 ? "-Model=Z 5_2" : "-Model=Z 6_3", file], ct);
            }
        }

        await host.UpdateSettingsAsync(s => s with { Rights = s.Rights with { Creator = "Luca Rossi", Copyright = "© {anno} Luca Rossi" } }, ct);
        shell.ShowSection(SectionCatalog.Home);
        var wizard = new ImportWizardViewModel(host, shell, null, card);
        shell.ShowPage(wizard);
        await Wait(() => wizard.IsLoaded && wizard.HasCameras, 60);
        wizard.EventName = "Matrimonio Sara e Luca";
        wizard.Cameras.Last().OffsetText = "+5";
        await Shot("s12-import-orari");
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);
        await Shot("s13-import-autore");

        // S24: photos opened from a folder of the disk, without importing them.
        var loose = Path.Combine(drive, "Da sistemare");
        Directory.CreateDirectory(loose);
        foreach (var name in new[] { "DSC_0420.jpg", "DSC_0401.jpg", "DSC_0402.jpg" })
        {
            var source = await PhotoFlow.Core.Catalog.AssetFiles.LocateAsync(host.Catalog, host.FileSystem, byFile[name], ct);
            File.Copy(source!, Path.Combine(loose, name.Replace("DSC_", "IMG_", StringComparison.Ordinal)), overwrite: true);
        }

        shell.ShowSection(SectionCatalog.Develop);
        var fromDisk = Assert.IsType<DevelopViewModel>(shell.CurrentPage);
        await fromDisk.OpenFilesAsync([loose]);
        await Wait(() => fromDisk.HasPhoto && fromDisk.After is not null && fromDisk.Histogram is not null && !fromDisk.IsComputing && !fromDisk.IsBusy
                         && fromDisk.Photos.All(p => p.Thumbnail is not null), 180);
        await Shot("s14-sviluppo-dal-disco");
    }

    public void Dispose() => _temp.Dispose();
}
