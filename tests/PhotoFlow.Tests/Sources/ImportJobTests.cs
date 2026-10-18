// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Ingest;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Sources;
using PhotoFlow.Data;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Sources.Scanning;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Sources;

/// <summary>Records every processed path; can cancel the run after a number of photos (simulated pause).</summary>
internal sealed class RecordingProcessor : IPhotoProcessor
{
    public ConcurrentQueue<string> Processed { get; } = new();

    public Func<string, bool> Fail { get; set; } = _ => false;

    public Action<int>? AfterProcessed { get; set; }

    public Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct)
    {
        var path = photo.Path;
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Processing a file that does not exist: {path}");
        }

        if (Fail(path))
        {
            throw new InvalidDataException("Corrupt preview.");
        }

        Processed.Enqueue(path);
        AfterProcessed?.Invoke(Processed.Count);
        var face = new Face(0, 0, new BoundingBox(0.1f, 0.1f, 0.3f, 0.4f), null, 0.9, EyeState.Open, EyeState.Open, null, null, null, null, "test@1");
        return Task.FromResult(new PhotoProcessingResult(
            new AnalyzerResult("culling-v1", "test@1", new Dictionary<string, double> { ["sharpness.zone"] = 0.7 }),
            [face],
            new DecisionProposal(path.EndsWith(".JPG", StringComparison.OrdinalIgnoreCase) ? CullingOutcome.ToReview : CullingOutcome.Kept, ["test"]),
            "decision-test"));
    }
}

[Trait(TestCategory.Name, TestCategory.Scenario)]
public sealed class ImportJobTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 10, 0, 0, TimeSpan.FromHours(2));
    private readonly TempFolder _temp = new("pf-job");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_import_copies_analyzes_each_photo_once_and_records_the_decisions()
    {
        var (card, library) = Card(photos: 3, withPair: true, withVideo: true);
        await using var catalog = await OpenCatalogAsync();
        var processor = new RecordingProcessor();
        var jobId = await CreateJobAsync(catalog, card, library);
        var before = FolderSnapshot.Take(card);

        var summary = await Runner(catalog, processor).RunAsync(jobId, null, Ct);

        Assert.Equal(before, FolderSnapshot.Take(card)); // I1: the card is untouched

        Assert.Equal(JobState.Completed, summary.State);
        Assert.Equal(0, summary.Failed);
        var items = await catalog.Jobs.GetItemsAsync(jobId, Ct);
        Assert.All(items, i => Assert.Equal(JobItemState.Done, i.State));
        Assert.Equal(6, items.Count); // DSC_0001 NEF+JPG, DSC_0002, DSC_0003, DSC_0100.MOV, DSC_0101
        // One analysis per photo: the pair is analyzed once, on its JPEG; the video is not analyzed.
        Assert.Equal(4, processor.Processed.Count);
        Assert.Contains(processor.Processed, p => p.EndsWith(".JPG", StringComparison.Ordinal));
        Assert.DoesNotContain(processor.Processed, p => p.EndsWith(".MOV", StringComparison.Ordinal));
        Assert.All(processor.Processed, p => Assert.StartsWith(library, p, StringComparison.OrdinalIgnoreCase)); // only verified copies (I3)

        // Both files of the pair carry the decision; the analysis and the faces are stored once.
        var pairItems = items.Where(i => Path.GetFileNameWithoutExtension(i.SourcePath) == "DSC_0001").ToList();
        Assert.Equal(2, pairItems.Count);
        foreach (var item in pairItems)
        {
            Assert.Equal(CullingOutcome.ToReview, (await catalog.Decisions.GetAsync(item.AssetId!.Value, Ct))?.AutomaticOutcome);
        }

        var jpeg = pairItems.Single(i => i.SourcePath.EndsWith(".JPG", StringComparison.Ordinal));
        Assert.NotNull(await catalog.Analysis.GetResultAsync(jpeg.AssetId!.Value, "culling-v1", Ct));
        Assert.Single(await catalog.Analysis.GetFacesAsync(jpeg.AssetId!.Value, Ct));
        Assert.All(items, i => Assert.True(File.Exists(Path.Combine(library, i.TargetRelativePath!)), i.TargetRelativePath));
    }

    [Fact]
    public async Task A_paused_job_resumes_without_processing_completed_photos_again()
    {
        var (card, library) = Card(photos: 24, withPair: false, withVideo: false);
        await using var catalog = await OpenCatalogAsync();
        var jobId = await CreateJobAsync(catalog, card, library);

        using var pause = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        // Enough photos that the pause lands while some are still waiting, even on a busy machine.
        var first = new RecordingProcessor { AfterProcessed = n => CancelAt(n, 2, pause) };
        var paused = await Runner(catalog, first).RunAsync(jobId, null, pause.Token);
        Assert.Equal(JobState.Paused, paused.State);
        Assert.Equal(JobPauseReason.UserRequest, paused.PauseReason);
        Assert.Equal(JobState.Paused, (await catalog.Jobs.GetAsync(jobId, Ct))!.State);

        var afterPause = await catalog.Jobs.GetItemsAsync(jobId, Ct);
        var done = afterPause.Where(i => i.State == JobItemState.Done).Select(i => i.TargetRelativePath).ToHashSet();
        Assert.NotEmpty(done);

        var second = new RecordingProcessor();
        var resumed = await Runner(catalog, second).RunAsync(jobId, null, Ct);

        Assert.Equal(JobState.Completed, resumed.State);
        Assert.All(await catalog.Jobs.GetItemsAsync(jobId, Ct), i => Assert.Equal(JobItemState.Done, i.State));
        Assert.DoesNotContain(second.Processed, p => done.Contains(Path.GetRelativePath(library, p)));
        Assert.Equal(24, first.Processed.Count(p => done.Contains(Path.GetRelativePath(library, p))) + second.Processed.Count);
        Assert.Equal(24, Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories).Count(f => !f.EndsWith(CopyEngine.TempExtension, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_file_whose_copy_fails_verification_never_reaches_the_analysis()
    {
        var (card, library) = Card(photos: 3, withPair: false, withVideo: false);
        await using var catalog = await OpenCatalogAsync();
        var jobId = await CreateJobAsync(catalog, card, library);
        var faulty = new FaultInjectingFileSystem(new PhysicalFileSystem())
        {
            CorruptWrites = p => p.StartsWith(library, StringComparison.OrdinalIgnoreCase) && p.Contains("_0002", StringComparison.Ordinal),
        };
        var processor = new RecordingProcessor();

        var summary = await Runner(catalog, processor, fileSystem: faulty).RunAsync(jobId, null, Ct);

        Assert.Equal(JobState.Completed, summary.State);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(2, processor.Processed.Count);
        Assert.DoesNotContain(processor.Processed, p => p.Contains("_0002", StringComparison.Ordinal));
        var failed = Assert.Single(await catalog.Jobs.GetItemsAsync(jobId, Ct), i => i.State == JobItemState.Failed);
        Assert.Null(failed.AssetId);
        Assert.NotNull(failed.Error);
    }

    [Fact]
    public async Task A_photo_that_cannot_be_analyzed_fails_alone()
    {
        var (card, library) = Card(photos: 3, withPair: false, withVideo: false);
        await using var catalog = await OpenCatalogAsync();
        var jobId = await CreateJobAsync(catalog, card, library);
        var processor = new RecordingProcessor { Fail = p => p.Contains("_0002", StringComparison.Ordinal) };

        var summary = await Runner(catalog, processor).RunAsync(jobId, null, Ct);

        Assert.Equal(JobState.Completed, summary.State);
        Assert.Equal(1, summary.Failed);
        var failed = Assert.Single(await catalog.Jobs.GetItemsAsync(jobId, Ct), i => i.State == JobItemState.Failed);
        Assert.StartsWith("Analysis:", failed.Error, StringComparison.Ordinal);
        Assert.NotNull(failed.AssetId); // the verified copy is kept and recorded
    }

    [Fact]
    public async Task Without_the_card_copied_photos_are_analyzed_and_the_job_waits_for_the_rest()
    {
        var (card, library) = Card(photos: 4, withPair: false, withVideo: false);
        await using var catalog = await OpenCatalogAsync();
        var jobId = await CreateJobAsync(catalog, card, library);

        // First run: two files cannot be read (failing card), the other two are copied and analyzed.
        var failing = new FaultInjectingFileSystem(new PhysicalFileSystem())
        {
            FailReads = p => p.StartsWith(card, StringComparison.OrdinalIgnoreCase) && (p.EndsWith("_0003.NEF", StringComparison.Ordinal) || p.EndsWith("_0004.NEF", StringComparison.Ordinal)),
        };
        var first = new RecordingProcessor();
        Assert.Equal(2, (await Runner(catalog, first, fileSystem: failing).RunAsync(jobId, null, Ct)).Failed);

        // The card is removed: simulated by a file system that does not see it. The failed files wait for it.
        var removed = new HiddenFolderFileSystem(new PhysicalFileSystem(), card);
        var second = new RecordingProcessor();
        var summary = await Runner(catalog, second, fileSystem: removed).RunAsync(jobId, null, Ct);

        Assert.Equal(JobState.Paused, summary.State);
        Assert.Equal(JobPauseReason.SourceUnavailable, summary.PauseReason);
        Assert.Empty(second.Processed);
        var third = new RecordingProcessor();
        Assert.Equal(JobState.Completed, (await Runner(catalog, third).RunAsync(jobId, null, Ct)).State);
        Assert.Equal(2, first.Processed.Count);
        Assert.Equal(2, third.Processed.Count);
        Assert.Empty(first.Processed.Intersect(third.Processed, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Jobs_left_running_by_a_crash_become_paused_and_resumable()
    {
        var (card, library) = Card(photos: 1, withPair: false, withVideo: false);
        await using var catalog = await OpenCatalogAsync();
        var jobId = await CreateJobAsync(catalog, card, library);
        await catalog.Jobs.SetStateAsync(jobId, JobState.Running, Ct);

        var interrupted = await ImportJobRunner.RecoverInterruptedAsync(catalog, Ct);

        Assert.Equal(jobId, Assert.Single(interrupted).Id);
        Assert.Equal(JobState.Paused, (await catalog.Jobs.GetAsync(jobId, Ct))!.State);
        Assert.Equal(JobState.Completed, (await Runner(catalog, new RecordingProcessor()).RunAsync(jobId, null, Ct)).State);
    }

    private static void CancelAt(int processed, int limit, CancellationTokenSource source)
    {
        if (processed == limit)
        {
            source.Cancel();
        }
    }

    private static ImportJobRunner Runner(ICatalog catalog, IPhotoProcessor processor, IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new PhysicalFileSystem();
        return new ImportJobRunner(catalog, new IngestService(new CopyEngine(fs)), new FakeExifReader(p => Start), fs, processor);
    }

    private async Task<SqliteCatalog> OpenCatalogAsync() =>
        await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "backups")), Ct);

    private async Task<long> CreateJobAsync(ICatalog catalog, string card, string library)
    {
        var scanned = new List<ScannedFile>();
        await foreach (var f in new SourceScanner(new PhysicalFileSystem(), card).ScanAsync(Ct))
        {
            scanned.Add(f);
        }

        var times = scanned.Select((f, i) => (f.Item.FullPath, Time: Start.AddSeconds(i))).ToDictionary(x => x.FullPath, x => x.Time);
        var plan = await new ImportPlanner(new PhysicalFileSystem(), new FakeExifReader(p => times[p]), catalog.Assets)
            .PlanAsync(scanned, "Test", null, new PathTemplate(PathTemplate.Default), Ct);
        var jobId = await ImportJobs.CreateAsync(catalog, plan, ImportSelection.All, card,
            [new IngestDestination("library", library, DestinationKind.Primary)], "Test", plan.ProposedEventDate, "events", null, Ct);
        return jobId;
    }

    /// <summary>Card with photos DSC_0001..N (NEF), optionally a JPEG twin of DSC_0001 and a video.</summary>
    private (string Card, string Library) Card(int photos, bool withPair, bool withVideo)
    {
        var card = _temp.Sub("card");
        var dcim = Directory.CreateDirectory(Path.Combine(card, "DCIM", "100NIKON")).FullName;
        for (var i = 1; i <= photos; i++)
        {
            Write(Path.Combine(dcim, $"DSC_{i:0000}.NEF"), [0x4D, 0x4D, 0x00, 0x2A], i);
        }

        if (withPair)
        {
            Write(Path.Combine(dcim, "DSC_0001.JPG"), [0xFF, 0xD8, 0xFF, 0xE1], 100);
        }

        if (withVideo)
        {
            Write(Path.Combine(dcim, "DSC_0100.MOV"), [0, 0, 0, 0x14, 0x66, 0x74, 0x79, 0x70, 0x71, 0x74, 0x20, 0x20], 101);
            Write(Path.Combine(dcim, "DSC_0101.NEF"), [0x4D, 0x4D, 0x00, 0x2A], 102);
        }

        return (card, _temp.Sub("library"));
    }

    private static void Write(string path, byte[] header, int seed)
    {
        var bytes = new byte[64 * 1024];
        new Random(seed).NextBytes(bytes);
        header.CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A file system where a folder (the removed card) does not exist.</summary>
    private sealed class HiddenFolderFileSystem(IFileSystem inner, string hidden) : IFileSystem
    {
        private bool Hidden(string path) => path.StartsWith(hidden, StringComparison.OrdinalIgnoreCase);

        public bool FileExists(string path) => !Hidden(path) && inner.FileExists(path);

        public bool DirectoryExists(string path) => !Hidden(path) && inner.DirectoryExists(path);

        public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption option) =>
            Hidden(directory) ? throw new DirectoryNotFoundException(directory) : inner.EnumerateFiles(directory, searchPattern, option);

        public FileMetadata GetFileMetadata(string path) => Hidden(path) ? throw new FileNotFoundException(path) : inner.GetFileMetadata(path);

        public Stream OpenRead(string path) => Hidden(path) ? throw new FileNotFoundException(path) : inner.OpenRead(path);

        public Stream OpenWrite(string path) => inner.OpenWrite(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void Move(string sourcePath, string destinationPath) => inner.Move(sourcePath, destinationPath);

        public void Replace(string replacementPath, string destinationPath, string? backupPath) => inner.Replace(replacementPath, destinationPath, backupPath);

        public long GetAvailableFreeSpace(string path) => inner.GetAvailableFreeSpace(path);
    }
}
