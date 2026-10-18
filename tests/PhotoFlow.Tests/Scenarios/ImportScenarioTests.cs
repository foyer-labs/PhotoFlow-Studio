// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Ingest;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Sources;
using PhotoFlow.Data;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Sources.Scanning;
using PhotoFlow.Tests.Sources;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Scenarios;

/// <summary>Acceptance scenarios of phase 1 (spec §13): 1,000 files, double import, forced close and resume.</summary>
[Trait(TestCategory.Name, TestCategory.Scenario)]
public sealed class ImportScenarioTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 9, 0, 0, TimeSpan.FromHours(2));
    private readonly TempFolder _temp = new("pf-scenario");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CatalogPath => Path.Combine(_temp.Path, "catalog.pfdb");

    private string Backups => Path.Combine(_temp.Path, "backups");

    [Fact]
    public async Task A_thousand_files_are_all_copied_verified_and_analyzed()
    {
        var card = Card(1000);
        var library = _temp.Sub("library");
        var before = FolderSnapshot.Take(card);
        long jobId;
        await using (var catalog = await OpenAsync())
        {
            jobId = await CreateJobAsync(catalog, card, library, ImportSelection.OnlyNew);
            var processor = new RecordingProcessor();
            var fs = new PhysicalFileSystem();
            var summary = await new ImportJobRunner(catalog, new IngestService(new CopyEngine(fs)), new FakeExifReader(Captured), fs, processor).RunAsync(jobId, null, Ct);

            Assert.Equal(JobState.Completed, summary.State);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(1000, processor.Processed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(1000, processor.Processed.Count);
        }

        Assert.Equal(before, FolderSnapshot.Take(card));
        AssertEveryFileHasAnIdenticalCopy(card, library);
    }

    [Fact]
    public async Task Importing_the_same_card_twice_creates_no_duplicates()
    {
        var card = Card(20);
        var library = _temp.Sub("library");
        await using var catalog = await OpenAsync();
        var fs = new PhysicalFileSystem();
        var first = await CreateJobAsync(catalog, card, library, ImportSelection.OnlyNew);
        await Runner(catalog, fs).RunAsync(first, null, Ct);

        // Second insertion of the card: nothing is new.
        var plan = await PlanAsync(catalog, card);
        Assert.Equal(0, plan.NewPhotos);
        Assert.Equal(20, plan.AlreadyImportedPhotos);
        Assert.Empty(plan.Selected(ImportSelection.OnlyNew));

        // Even forcing "All" (P9), identical files already in place are recognized: no second copy, no second asset.
        var forced = await CreateJobAsync(catalog, card, library, ImportSelection.All);
        var summary = await Runner(catalog, fs).RunAsync(forced, null, Ct);
        Assert.Equal(JobState.Completed, summary.State);
        Assert.Equal(20, Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories).Count());
        var assets = (await catalog.Jobs.GetItemsAsync(first, Ct)).Select(i => i.AssetId).ToHashSet();
        Assert.Equal(assets, (await catalog.Jobs.GetItemsAsync(forced, Ct)).Select(i => i.AssetId).ToHashSet());
    }

    [Fact]
    public async Task An_import_killed_midway_resumes_without_losing_or_redoing_photos()
    {
        var card = Card(80);
        var library = _temp.Sub("library");
        long jobId;
        await using (var catalog = await OpenAsync())
        {
            jobId = await CreateJobAsync(catalog, card, library, ImportSelection.OnlyNew);
        }

        // First run: killed (whole process) after some photos are completed.
        var firstLog = Path.Combine(_temp.Path, "processed-1.log");
        using (var host = ScenarioHostProcess.Start(CatalogPath, Backups, jobId, firstLog, delayMs: 40))
        {
            await ScenarioHostProcess.WaitForAsync(host, line => line.StartsWith("copied", StringComparison.Ordinal) && ScenarioHostProcess.Completed(line) >= 15 ? "kill" : null);
            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync(Ct);
        }

        HashSet<string> doneBeforeKill;
        await using (var catalog = await OpenAsync())
        {
            var items = await catalog.Jobs.GetItemsAsync(jobId, Ct);
            Assert.Equal(JobState.Running, (await catalog.Jobs.GetAsync(jobId, Ct))!.State); // left running by the crash
            doneBeforeKill = items.Where(i => i.State == JobItemState.Done).Select(i => Path.Combine(library, i.TargetRelativePath!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.InRange(doneBeforeKill.Count, 15, 79);
        }

        // Second run: the interrupted job is detected and resumed to the end.
        var secondLog = Path.Combine(_temp.Path, "processed-2.log");
        using (var host = ScenarioHostProcess.Start(CatalogPath, Backups, jobId, secondLog, delayMs: 0))
        {
            var lines = new List<string>();
            await ScenarioHostProcess.WaitForAsync(host, line => { lines.Add(line); return null; });
            await host.WaitForExitAsync(Ct);
            Assert.Equal(0, host.ExitCode);
            Assert.Contains("interrupted 1", lines);
            Assert.Contains("state Completed", lines);
        }

        await using (var catalog = await OpenAsync())
        {
            Assert.All(await catalog.Jobs.GetItemsAsync(jobId, Ct), i => Assert.Equal(JobItemState.Done, i.State));
        }

        var redone = File.ReadAllLines(secondLog).Where(doneBeforeKill.Contains).ToList();
        Assert.Empty(redone);
        var processed = File.ReadAllLines(firstLog).Concat(File.ReadAllLines(secondLog)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(80, processed);
        AssertEveryFileHasAnIdenticalCopy(card, library);
        Assert.Empty(Directory.EnumerateFiles(library, "*" + CopyEngine.TempExtension, SearchOption.AllDirectories));
    }

    /// <summary>Distinct capture times from the file number (DSC_0001 → +1 s), as a camera would write them.</summary>
    private static DateTimeOffset? Captured(string path) =>
        Start.AddSeconds(int.Parse(Path.GetFileNameWithoutExtension(path)[4..], CultureInfo.InvariantCulture));

    private static void AssertEveryFileHasAnIdenticalCopy(string card, string library)
    {
        var copies = Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories).Select(FolderSnapshot.Sha256).ToList();
        var originals = Directory.EnumerateFiles(card, "*", SearchOption.AllDirectories).Select(FolderSnapshot.Sha256).ToList();
        Assert.Equal(originals.Count, copies.Count);
        Assert.Equal(originals.Order(StringComparer.Ordinal), copies.Order(StringComparer.Ordinal));
    }

    private ImportJobRunner Runner(ICatalog catalog, IFileSystem fs) =>
        new(catalog, new IngestService(new CopyEngine(fs)), new FakeExifReader(Captured), fs, new RecordingProcessor());

    private Task<SqliteCatalog> OpenAsync() => ScenarioHostProcess.OpenCatalogAsync(CatalogPath, Backups);

    private async Task<long> CreateJobAsync(ICatalog catalog, string card, string library, ImportSelection selection)
    {
        var plan = await PlanAsync(catalog, card);
        return await ImportJobs.CreateAsync(catalog, plan, selection, card, [new IngestDestination("library", library, DestinationKind.Primary)],
            "Scenario", plan.ProposedEventDate, "events", null, Ct);
    }

    private static async Task<ImportPlan> PlanAsync(ICatalog catalog, string card)
    {
        var scanned = new List<ScannedFile>();
        await foreach (var f in new SourceScanner(new PhysicalFileSystem(), card).ScanAsync(Ct))
        {
            scanned.Add(f);
        }

        return await new ImportPlanner(new PhysicalFileSystem(), new FakeExifReader(Captured), catalog.Assets)
            .PlanAsync(scanned, "Scenario", null, new PathTemplate(PathTemplate.Default), Ct);
    }

    /// <summary>A card with <paramref name="count"/> synthetic NEF files (TIFF header, random content).</summary>
    private string Card(int count)
    {
        var card = _temp.Sub("card");
        var dcim = Directory.CreateDirectory(Path.Combine(card, "DCIM", "100NIKON")).FullName;
        var bytes = new byte[32 * 1024];
        for (var i = 1; i <= count; i++)
        {
            new Random(i).NextBytes(bytes);
            bytes[0] = 0x4D;
            bytes[1] = 0x4D;
            bytes[2] = 0x00;
            bytes[3] = 0x2A;
            File.WriteAllBytes(Path.Combine(dcim, $"DSC_{i:0000}.NEF"), bytes);
        }

        return card;
    }

    public void Dispose() => _temp.Dispose();
}
