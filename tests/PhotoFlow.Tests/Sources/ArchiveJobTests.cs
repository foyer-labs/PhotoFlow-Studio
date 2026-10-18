// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Data;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Sources;

/// <summary>In-place analysis of an existing library (phase 1b): read only, resumable, no redone work, I5 kept.</summary>
[Trait(TestCategory.Name, TestCategory.Scenario)]
public sealed class ArchiveJobTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-archive");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_archive_is_analyzed_in_place_without_touching_its_files()
    {
        var (root, _) = Archive(photos: 5, corrupt: true, wrongExtension: true);
        var before = FolderSnapshot.Take(root);
        await using var catalog = await OpenAsync();
        var libraryId = await AddLibraryAsync(catalog, root);
        var processor = new RecordingProcessor { Fail = p => p.EndsWith("broken.jpg", StringComparison.Ordinal) };
        var jobId = await ArchiveJobRunner.CreateAsync(catalog, new PhysicalFileSystem(), new ArchiveJobSettings(libraryId, "events"), Ct);

        var summary = await Runner(catalog, processor).RunAsync(jobId, null, Ct);

        Assert.Equal(before, FolderSnapshot.Take(root)); // nothing written or deleted in the library
        Assert.Equal(JobState.Completed, summary.State);
        Assert.Equal(1, summary.Failed);
        var items = await catalog.Jobs.GetItemsAsync(jobId, Ct);
        Assert.Equal(7, items.Count); // 5 photos + broken.jpg + renamed.png (a JPEG)
        var failed = Assert.Single(items, i => i.State == JobItemState.Failed);
        Assert.EndsWith("broken.jpg", failed.SourcePath, StringComparison.Ordinal);
        Assert.Contains(processor.Processed, p => p.EndsWith("renamed.png", StringComparison.Ordinal));
        Assert.All(processor.Processed, p => Assert.StartsWith(root, p, StringComparison.OrdinalIgnoreCase)); // in place, no copy

        // Every analyzed photo has a location in the library and, after the final pass, its full hash.
        foreach (var item in items.Where(i => i.State == JobItemState.Done))
        {
            var asset = await catalog.Assets.GetAsync(item.AssetId!.Value, Ct);
            Assert.Equal(FullHashOf(item.SourcePath), asset!.FullHash);
            Assert.Equal(item.TargetRelativePath, Assert.Single(await catalog.Assets.GetLocationsAsync(asset.Id, Ct)).RelativePath);
        }
    }

    [Fact]
    public async Task A_paused_archive_job_resumes_without_redoing_photos_and_keeps_manual_decisions()
    {
        var (root, _) = Archive(photos: 12, corrupt: false, wrongExtension: false);
        await using var catalog = await OpenAsync();
        var libraryId = await AddLibraryAsync(catalog, root);
        var jobId = await ArchiveJobRunner.CreateAsync(catalog, new PhysicalFileSystem(), new ArchiveJobSettings(libraryId, "events", AnalysisWorkers: 2), Ct);

        using var pause = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var first = new RecordingProcessor { AfterProcessed = n => { if (n == 4) { pause.Cancel(); } } };
        Assert.Equal(JobState.Paused, (await Runner(catalog, first).RunAsync(jobId, null, pause.Token)).State);
        var done = (await catalog.Jobs.GetItemsAsync(jobId, Ct)).Where(i => i.State == JobItemState.Done).ToList();
        Assert.NotEmpty(done);

        // The user decides on a photo meanwhile (I5): a later automatic proposal never overwrites it.
        var decided = done[0].AssetId!.Value;
        await catalog.Decisions.SetUserAsync(decided, CullingOutcome.Kept, 5, null, Ct);

        var second = new RecordingProcessor();
        Assert.Equal(JobState.Completed, (await Runner(catalog, second).RunAsync(jobId, null, Ct)).State);
        Assert.Empty(second.Processed.Intersect(done.Select(d => d.SourcePath), StringComparer.OrdinalIgnoreCase));
        Assert.Equal(12, first.Processed.Concat(second.Processed).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var decision = await catalog.Decisions.GetAsync(decided, Ct);
        Assert.Equal(CullingOutcome.Kept, decision!.UserOutcome);
        Assert.Equal(5, decision.Rating);
    }

    [Fact]
    public async Task Photos_already_analyzed_are_not_analyzed_again()
    {
        var (root, _) = Archive(photos: 3, corrupt: false, wrongExtension: false);
        await using var catalog = await OpenAsync();
        var libraryId = await AddLibraryAsync(catalog, root);
        var fs = new PhysicalFileSystem();
        var firstJob = await ArchiveJobRunner.CreateAsync(catalog, fs, new ArchiveJobSettings(libraryId, "events"), Ct);
        await Runner(catalog, new RecordingProcessor()).RunAsync(firstJob, null, Ct);

        var again = new RecordingProcessor();
        var secondJob = await ArchiveJobRunner.CreateAsync(catalog, fs, new ArchiveJobSettings(libraryId, "events"), Ct);
        var summary = await Runner(catalog, again).RunAsync(secondJob, null, Ct);

        Assert.Equal(JobState.Completed, summary.State);
        Assert.Empty(again.Processed);
        Assert.All(await catalog.Jobs.GetItemsAsync(secondJob, Ct), i => Assert.Equal(JobItemState.Done, i.State));
    }

    [Fact]
    public async Task Newest_files_are_planned_first()
    {
        var (root, files) = Archive(photos: 4, corrupt: false, wrongExtension: false);
        for (var i = 0; i < files.Count; i++)
        {
            File.SetLastWriteTimeUtc(files[i], new DateTime(2020 + i, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        await using var catalog = await OpenAsync();
        var libraryId = await AddLibraryAsync(catalog, root);
        var jobId = await ArchiveJobRunner.CreateAsync(catalog, new PhysicalFileSystem(), new ArchiveJobSettings(libraryId, "events", ArchiveOrder.NewestFirst), Ct);

        var planned = (await catalog.Jobs.GetItemsAsync(jobId, Ct)).OrderBy(i => i.Id).Select(i => i.SourcePath).ToList();
        Assert.Equal(files.AsEnumerable().Reverse(), planned);
    }

    [Fact]
    public async Task A_library_that_disappears_midway_pauses_the_job_instead_of_failing_its_photos()
    {
        var (root, _) = Archive(photos: 10, corrupt: false, wrongExtension: false);
        await using var catalog = await OpenAsync();
        var libraryId = await AddLibraryAsync(catalog, root);
        var nas = new SwitchableFileSystem(new PhysicalFileSystem(), root);
        var jobId = await ArchiveJobRunner.CreateAsync(catalog, nas, new ArchiveJobSettings(libraryId, "events", AnalysisWorkers: 1), Ct);

        // The NAS goes away after three photos.
        var first = new RecordingProcessor { AfterProcessed = n => { if (n == 3) { nas.Gone = true; } } };
        var summary = await new ArchiveJobRunner(catalog, new FakeExifReader(_ => null), nas, first).RunAsync(jobId, null, Ct);

        Assert.Equal(JobState.Paused, summary.State);
        Assert.Equal(JobPauseReason.SourceUnavailable, summary.PauseReason);
        var items = await catalog.Jobs.GetItemsAsync(jobId, Ct);
        Assert.DoesNotContain(items, i => i.State == JobItemState.Failed);
        Assert.InRange(items.Count(i => i.State == JobItemState.Done), 3, 9);

        // Back online: the job resumes and finishes.
        nas.Gone = false;
        var second = new RecordingProcessor();
        Assert.Equal(JobState.Completed, (await new ArchiveJobRunner(catalog, new FakeExifReader(_ => null), nas, second).RunAsync(jobId, null, Ct)).State);
        Assert.All(await catalog.Jobs.GetItemsAsync(jobId, Ct), i => Assert.Equal(JobItemState.Done, i.State));
        Assert.Equal(10, first.Processed.Concat(second.Processed).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    public void Dispose() => _temp.Dispose();

    private static ArchiveJobRunner Runner(ICatalog catalog, RecordingProcessor processor)
    {
        var fs = new PhysicalFileSystem();
        return new ArchiveJobRunner(catalog, new FakeExifReader(_ => null), fs, processor);
    }

    /// <summary>A file system where a folder (a NAS share) can disappear and come back.</summary>
    private sealed class SwitchableFileSystem(IFileSystem inner, string folder) : IFileSystem
    {
        public volatile bool Gone;

        private bool Hidden(string path) => Gone && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);

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

    private async Task<SqliteCatalog> OpenAsync() =>
        await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);

    private static Task<long> AddLibraryAsync(ICatalog catalog, string root) =>
        catalog.Libraries.AddAsync(new Library(0, "Archivio", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);

    private static string FullHashOf(string path) =>
        QuickHash.ComputeFullAsync(new PhysicalFileSystem(), path, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>An archive with nested folders: JPEG photos, optionally a truncated JPEG and a JPEG named .png.</summary>
    private (string Root, List<string> Files) Archive(int photos, bool corrupt, bool wrongExtension)
    {
        var root = _temp.Sub("archive");
        var files = new List<string>();
        for (var i = 1; i <= photos; i++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, $"{2015 + (i % 3)}", $"evento-{i % 2}")).FullName;
            files.Add(SyntheticImages.WriteJpeg(Path.Combine(folder, $"IMG_{i:0000}.JPG"), 120, 80, i));
        }

        if (corrupt)
        {
            var broken = Path.Combine(root, "broken.jpg");
            SyntheticImages.WriteJpeg(broken, 120, 80, 99);
            File.WriteAllBytes(broken, File.ReadAllBytes(broken)[..200]);
        }

        if (wrongExtension)
        {
            SyntheticImages.WriteJpeg(Path.Combine(root, "renamed.png"), 120, 80, 77);
        }

        return (root, files);
    }
}
