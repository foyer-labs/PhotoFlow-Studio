// SPDX-License-Identifier: Apache-2.0

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// The regeneration page after a change of recognizer (P22): the whole catalog is proposed; "Riparti da zero con le
/// persone" shows the counts and waits for an explicit confirmation before the regeneration can start; the preview
/// counts again what will go; "Applica" backs up the catalog and removes the people, in every library.
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class RegenerationResetTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-regeneration-reset");

    [AvaloniaFact]
    public async Task The_reset_of_the_people_needs_the_counts_and_a_confirmation()
    {
        var ct = CancellationToken.None;
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        await using var host = await PhotoFlowHost.StartAsync(paths, ct);
        await host.UpdateSettingsAsync(s => s with { SetupCompleted = true }, ct);
        MarkInstalled(host, "opencv-yunet-2023mar");
        MarkInstalled(host, PhotoFlowHost.SFaceModelId);
        host.ProcessorOverride = new SameFaces();
        var catalog = host.Catalog;
        var ids = new List<long>();
        foreach (var name in new[] { "a", "b" })
        {
            var root = _temp.Sub("library-" + name);
            SyntheticImages.WriteJpeg(Path.Combine(root, "p.jpg"), 200, 150);
            var libraryId = await catalog.Libraries.AddAsync(new Library(0, name, root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), ct);
            var id = await catalog.Assets.AddAsync(new Asset(0, "q" + name, "f" + name, "JPG", "jpeg", 200, 150, null, DateSource.Unknown, null, AssetState.Active), ct);
            await catalog.Assets.AddLocationAsync(new AssetLocation(0, id, libraryId, "p.jpg", DateTimeOffset.UtcNow), ct);
            await catalog.Analysis.ReplaceAutomaticFacesAsync(id, [SameFaces.Face(id, "arc@1/arcface")], ct);
            ids.Add(id);
        }

        var anna = await catalog.People.CreatePersonAsync("Anna", ct);
        await catalog.People.AssignManualAsync([(await catalog.Analysis.GetFacesAsync(ids[0], ct)).Single().Id], anna, ct);

        using var page = new RegenerationViewModel(host, null, parts: ReanalysisParts.Faces, note: "AdaFace", allCatalog: true);
        await page.ActivateAsync();
        Assert.True(page.IsAllCatalog);
        Assert.True(page.CanResetPeople);

        page.ResetPeople = true;
        await WaitAsync(() => page.ResetText is not null);
        Assert.Equal(Strings.Format("Regeneration.Reset.Counts", "1", "1", "1", "0"), page.ResetText);
        Assert.True(page.AsksResetConfirmation);

        // Not confirmed: nothing starts.
        await page.RunCommand.ExecuteAsync(null);
        Assert.Null(page.JobId);
        Assert.Equal(Strings.Get("Regeneration.Reset.ConfirmFirst"), page.ResultText);
        Assert.Single(await catalog.People.ListPersonsAsync(ct));

        page.ConfirmResetCommand.Execute(null);
        await page.RunCommand.ExecuteAsync(null);
        var settings = ReanalysisJobRunner.DeserializeSettings((await catalog.Jobs.GetAsync(page.JobId!.Value, ct))!.SettingsSnapshotJson);
        Assert.Equal((true, true, null, ReanalysisParts.Faces), (settings.AllCatalog, settings.ResetPeople, settings.LibraryId, settings.Parts));
        await WaitAsync(() => !page.IsRunning); // the real models are not here: the job the page started stops

        // The same regeneration run with the new recognizer standing in, opened on its preview as from Jobs.
        var jobId = await ReanalysisJobRunner.CreateAsync(catalog, settings, ct);
        var versions = new ModelVersions("culling-v1", "x", null, "new@2/sface", null, null, null);
        await new ReanalysisJobRunner(catalog, host.FileSystem, new SameFaces(), _ => Task.FromResult(versions)).RunAsync(jobId, null, ct);
        using var preview = new RegenerationViewModel(host, null, reanalysisJobId: jobId);
        await preview.ActivateAsync();
        Assert.Equal(2, preview.Preview!.Photos);
        Assert.Contains(Strings.Get("Regeneration.Reset.Preview") + " " + page.ResetText, preview.PreviewLines);
        Assert.Single(await catalog.People.ListPersonsAsync(ct)); // still there until "Applica"

        await preview.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(await catalog.People.ListPersonsAsync(ct));
        foreach (var id in ids)
        {
            Assert.Null((await catalog.Analysis.GetFacesAsync(id, ct)).Single().PersonId);
        }

        Assert.Contains(Directory.GetFiles(paths.BackupsDirectory), f => f.Contains("people-reset", StringComparison.Ordinal));
        Assert.Contains(Strings.Format("Regeneration.Reset.Done", "1", "1", "0"), preview.ResultText, StringComparison.Ordinal);
    }

    public void Dispose() => _temp.Dispose();

    private static void MarkInstalled(PhotoFlowHost host, string id)
    {
        var model = host.Models.Get(id);
        var folder = host.Models.InstallDirectory(id);
        Directory.CreateDirectory(folder);
        foreach (var file in model.Files.SelectMany(f => f.Use.Count > 0 ? f.Use : [f.Path]))
        {
            File.WriteAllBytes(Path.Combine(folder, file), [1]);
        }

        File.WriteAllText(Path.Combine(folder, ".installed"), model.Version);
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

    /// <summary>The new recognizer finds the same face (no fingerprint: the keys are not needed).</summary>
    private sealed class SameFaces : IPhotoProcessor
    {
        public static Face Face(long assetId, string embeddingModel) =>
            new(0, assetId, new BoundingBox(0.1f, 0.2f, 0.3f, 0.5f), null, 0.9, EyeState.Open, EyeState.Open, null, null, null, null, "det@1", [new Point2(0.1f, 0.2f)],
                embeddingModel);

        public Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct) =>
            Task.FromResult(new PhotoProcessingResult(new AnalyzerResult("culling-v1", "x", new Dictionary<string, double>()), [Face(0, "new@2/sface")],
                new DecisionProposal(CullingOutcome.Kept, []), "x", null, null));
    }
}
