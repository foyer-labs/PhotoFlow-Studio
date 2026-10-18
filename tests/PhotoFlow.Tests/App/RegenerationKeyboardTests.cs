// SPDX-License-Identifier: Apache-2.0

using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Tagging;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// The regeneration page (spec §5): a regeneration that already ran is opened from Jobs on its preview (the catalog
/// still has the old results); Ctrl Enter applies it; the preview goes away and the result is told.
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class RegenerationKeyboardTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-regeneration-keys");

    [AvaloniaFact]
    public async Task The_preview_of_a_regeneration_is_applied_with_the_keyboard()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        await using var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None);
        await host.UpdateSettingsAsync(s => s with { SetupCompleted = true }, CancellationToken.None);
        var ct = CancellationToken.None;
        var catalog = host.Catalog;
        var root = _temp.Sub("library");
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "Archivio", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), ct);
        var ids = new List<long>();
        for (var i = 0; i < 2; i++)
        {
            SyntheticImages.WriteJpeg(Path.Combine(root, $"p{i}.jpg"), 200, 150);
            var id = await catalog.Assets.AddAsync(new Asset(0, $"q{i}", $"f{i}", "JPG", "jpeg", 200, 150, null, DateSource.Unknown, null, AssetState.Active), ct);
            await catalog.Assets.AddLocationAsync(new AssetLocation(0, id, libraryId, $"p{i}.jpg", DateTimeOffset.UtcNow), ct);
            await catalog.Tags.ReplaceAutomaticAsync(id, TagSource.RamPlus, "ram@1", [("lake", 0.9)], ct);
            ids.Add(id);
        }

        var versions = new ModelVersions("culling-v1", null, null, null, "ram@2", null, null);
        var jobId = await ReanalysisJobRunner.CreateAsync(catalog, new ReanalysisSettings(ReanalysisParts.Tags, LibraryId: libraryId), ct);
        await new ReanalysisJobRunner(catalog, new PhysicalFileSystem(), new NewTags(), _ => Task.FromResult(versions)).RunAsync(jobId, null, ct);

        using var shell = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        shell.ShowSection(SectionCatalog.Jobs);
        var jobs = Assert.IsType<JobsViewModel>(shell.CurrentPage);
        await WaitAsync(() => jobs.Completed.Any(j => j.Job.Id == jobId));
        jobs.Selected = jobs.Completed.Single(j => j.Job.Id == jobId);
        jobs.OpenReviewCommand.Execute(null);
        var page = Assert.IsType<RegenerationViewModel>(shell.CurrentPage);
        await WaitAsync(() => page.HasPreview);
        Assert.Equal(2, page.Preview!.Photos);
        Assert.Equal(2, page.Preview.TagsAdded);
        Assert.NotEmpty(page.PreviewLines);
        Assert.Equal("ram@1", (await catalog.Tags.GetForAssetAsync(ids[0], ct)).Single(t => t.Tag.Key == "lake").Assignment.ModelVersion);
        Save(window, "5g-regeneration-preview");

        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendantsOf<RegenerationView>()?.Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        await WaitAsync(() => page.ResultText is not null && !page.HasPreview);
        Assert.Contains(await catalog.Tags.GetForAssetAsync(ids[0], ct), t => t.Tag.Key == "sunset" && t.Assignment.ModelVersion == "ram@2");
        Assert.Equal(0, await catalog.Reanalysis.CountAsync(jobId, ct));
    }

    public void Dispose() => _temp.Dispose();

    private static void Save(Avalonia.Controls.Window window, string name)
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

    /// <summary>RAM++ 2 finds a sunset too.</summary>
    private sealed class NewTags : IPhotoProcessor
    {
        public Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct) =>
            Task.FromResult(new PhotoProcessingResult(new AnalyzerResult("culling-v1", "x", new Dictionary<string, double>()), [],
                new DecisionProposal(CullingOutcome.Kept, []), "x", null,
                [new TagResult("lake", TagSource.RamPlus, 0.9, "ram@2"), new TagResult("sunset", TagSource.RamPlus, 0.8, "ram@2")]));
    }
}
