// SPDX-License-Identifier: Apache-2.0

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PhotoFlow.App.Services.Develop;
using PhotoFlow.App.ViewModels;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Develop;
using PhotoFlow.Core.IO;
using PhotoFlow.Data;
using PhotoFlow.Imaging;
using PhotoFlow.Tests.Docs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Develop;

/// <summary>Photos opened from disk without importing them (decision S24): settings and exports outside the catalog.</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class DevelopFilesUnitTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-develop-files");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Files_from_disk_keep_settings_and_exports_outside_the_catalog()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var imported = await catalog.Assets.AddAsync(new Asset(0, "qa", "fa", "NEF", "raw", 6000, 4000, null, DateSource.Unknown, null, AssetState.Active), Ct);
        var data = _temp.Sub("dati");
        var photo = Path.Combine(_temp.Sub("foto"), "DSC_0001.NEF");
        File.WriteAllBytes(photo, [1, 2, 3]);
        var store = new DevelopFiles(catalog, new PhysicalFileSystem(), data);

        var loose = store.Register(photo);
        Assert.True(DevelopFiles.IsLoose(loose));
        Assert.Equal(loose, store.Register(photo.ToLowerInvariant())); // the same file, whatever the case
        Assert.Equal(photo, await store.LocateAsync(loose, Ct));
        Assert.Null(await store.GetSettingsAsync(loose, Ct));

        await store.SetSettingsAsync(loose, "{\"tone\":{\"contrast\":20}}", Ct);
        await store.SetSettingsAsync(imported, "{\"tone\":{\"contrast\":10}}", Ct);
        await store.AddExportAsync(new ExportRecord(loose, "web/DSC_0001.jpg", ExportRecipes.Web, DateTimeOffset.Now), Ct);
        Assert.Equal(new HashSet<long> { loose, imported }, await store.ListDevelopedAsync([loose, imported], Ct));
        Assert.Equal(new HashSet<long> { imported }, await catalog.Develop.ListDevelopedAsync([loose, imported], Ct));
        Assert.Empty(await catalog.Develop.LastExportsAsync([loose], Ct));

        // Another session finds the file, its settings and its export again; nothing was written next to the original.
        var again = new DevelopFiles(catalog, new PhysicalFileSystem(), data);
        Assert.Equal(photo, again.PathOf(loose));
        Assert.Equal("{\"tone\":{\"contrast\":20}}", await again.GetSettingsAsync(loose, Ct));
        Assert.Equal("web/DSC_0001.jpg", (await again.LastExportsAsync([loose, imported], Ct))[loose].OutputPath);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(photo)!));

        Assert.True(DevelopFiles.IsSupported("a.CR3"));
        Assert.True(DevelopFiles.IsSupported("a.jpeg"));
        Assert.False(DevelopFiles.IsSupported("a.xmp"));
    }

    public void Dispose() => _temp.Dispose();
}

/// <summary>The real engine: files from a folder open in Develop, are developed and exported, and the catalog stays as it was.</summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class DevelopFilesTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-develop-loose");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task Photos_not_imported_open_develop_and_export()
    {
        Assert.SkipUnless(DevelopEngineClient.IsAvailable, "Motore di sviluppo non disponibile (engine/.venv)");
        var (host, _, _) = await DocsScreenshots.PrepareAsync(_temp.Sub("demo"), Ct);
        await using var _ = host;
        var folder = _temp.Sub("Sciolte");
        WriteJpeg(Path.Combine(folder, "uno.jpg"), 60);
        WriteJpeg(Path.Combine(folder, "due.jpg"), 140);
        File.WriteAllText(Path.Combine(folder, "note.txt"), "non una foto");

        var navigator = new NullNavigator();
        using var develop = new DevelopViewModel(host, navigator);
        await develop.ActivateAsync();
        await develop.OpenFilesAsync([folder]);
        Assert.Equal(["due.jpg", "uno.jpg"], develop.Photos.Select(p => p.Name));
        Assert.All(develop.Photos, p => Assert.True(p.IsLoose && p.AssetId < 0));
        Assert.Contains("Sciolte", develop.Origin, StringComparison.Ordinal);
        await Wait(() => develop.HasPhoto && develop.After is not null && !develop.IsComputing, 120);
        await Wait(() => develop.Photos.All(p => p.Thumbnail is not null), 30);

        develop.Exposure = 0.7;
        Assert.True(await develop.SendToWorkingSetAsync(forExport: true)); // saves the pending change
        var ids = develop.Photos.Select(p => p.AssetId).ToList();
        Assert.Contains(ids[0], await host.DevelopFiles.ListDevelopedAsync(ids, Ct));
        Assert.Empty(await host.Catalog.Assets.GetManyAsync(ids, Ct));

        using var export = new ExportViewModel(host, navigator);
        await export.ActivateAsync();
        Assert.Equal(ids, export.SelectedScope!.AssetIds);
        export.SetFolder(_temp.Sub("Consegna"));
        await export.StartCommand.ExecuteAsync(null);
        await Wait(() => host.Exports.Runs.Count > 0 && host.Exports.Runs[0].State is ExportState.Completed or ExportState.Failed, 120);
        Assert.Equal(ExportState.Completed, host.Exports.Runs[0].State);
        var last = await host.DevelopFiles.LastExportsAsync(ids, Ct);
        Assert.Equal(2, last.Count);
        Assert.All(last.Values, r => Assert.True(File.Exists(r.OutputPath), r.OutputPath));
        Assert.Equal(3, Directory.GetFiles(folder).Length); // the originals untouched, nothing beside them
    }

    private static void WriteJpeg(string path, int level)
    {
        var random = new Random(level);
        var pixels = new byte[320 * 240 * 3];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)Math.Clamp(level + random.Next(-20, 21), 0, 255);
        }

        File.WriteAllBytes(path, new SkiaImageOperations().EncodeJpeg(new global::PhotoFlow.Core.Imaging.RgbImage(320, 240, pixels), 95));
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
