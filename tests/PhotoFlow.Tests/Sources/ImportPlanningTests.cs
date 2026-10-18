// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Ingest;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Sources;
using PhotoFlow.Data;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Sources.Scanning;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Sources;

/// <summary>EXIF reader with controlled capture times (synthetic files have no EXIF).</summary>
internal sealed class FakeExifReader(Func<string, DateTimeOffset?> captureTime) : IExifReader
{
    public Task<IReadOnlyList<ExifInfo>> ReadAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExifInfo>>(paths.Select(p =>
        {
            var at = captureTime(p);
            return new ExifInfo(p, Path.GetExtension(p).TrimStart('.').ToUpperInvariant(), "NIKON", "NIKON Z5_2", null, 100, 0.001, 2.8, 50, 0,
                at, at is null ? DateSource.Unknown : DateSource.Exif, 1, null, 6048, 4032, null, null, null);
        }).ToList());
}

/// <summary>Controllable clock for retry schedules.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class ImportPlanningTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 10, 2, 0, TimeSpan.FromHours(2));
    private readonly TempFolder _temp = new("pf-plan");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Plan_counts_pairs_numbers_by_shooting_order_and_recognizes_imported_photos()
    {
        var card = _temp.Sub("card");
        var folder = Directory.CreateDirectory(Path.Combine(card, "DCIM", "100NIKON")).FullName;
        Write(Path.Combine(folder, "DSC_0003.NEF"), [0x4D, 0x4D, 0x00, 0x2A], 1);
        Write(Path.Combine(folder, "DSC_0003.JPG"), [0xFF, 0xD8, 0xFF, 0xE1], 2);
        Write(Path.Combine(folder, "DSC_0001.NEF"), [0x4D, 0x4D, 0x00, 0x2A], 3);
        Write(Path.Combine(folder, "DSC_0002.MOV"), [0, 0, 0, 0x14, 0x66, 0x74, 0x79, 0x70, 0x71, 0x74, 0x20, 0x20], 4);
        var times = new Dictionary<string, DateTimeOffset>
        {
            ["DSC_0001.NEF"] = Start.AddMinutes(5),  // later than DSC_0003 despite the name
            ["DSC_0003.NEF"] = Start,
            ["DSC_0003.JPG"] = Start,
            ["DSC_0002.MOV"] = Start.AddMinutes(1),
        };
        var exif = new FakeExifReader(p => times[Path.GetFileName(p)]);
        var fs = new PhysicalFileSystem();
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);

        // DSC_0001 was imported before: same quick hash and capture time.
        var known = await QuickHash.ComputeAsync(fs, Path.Combine(folder, "DSC_0001.NEF"), Ct);
        await catalog.Assets.AddAsync(new Asset(0, known, null, "NEF", "raw", null, null, Start.AddMinutes(5), DateSource.Exif, null, AssetState.Active), Ct);

        var scanned = await ScanAsync(card);
        var plan = await new ImportPlanner(fs, exif, catalog.Assets).PlanAsync(scanned, "Matrimonio Sara e Luca", null, new PathTemplate(PathTemplate.Default), Ct);

        Assert.Equal(1, plan.NewPhotos);            // DSC_0003 (RAW+JPG pair)
        Assert.Equal(1, plan.AlreadyImportedPhotos); // DSC_0001
        Assert.Equal(1, plan.RawJpegPairs);
        Assert.Equal(1, plan.Videos);
        Assert.Equal(new DateOnly(2026, 10, 12), plan.ProposedEventDate);
        Assert.Equal(Start, plan.FirstCapture);
        var pair = plan.Files.Where(f => f.File.PairKey.EndsWith("DSC_0003", StringComparison.Ordinal)).ToList();
        Assert.All(pair, f => Assert.Equal(1, f.Sequence));
        Assert.Contains(pair, f => f.TargetRelativePath.EndsWith("20261012_Matrimonio-Sara-e-Luca_0001.NEF", StringComparison.Ordinal));
        Assert.Contains(pair, f => f.TargetRelativePath.EndsWith("20261012_Matrimonio-Sara-e-Luca_0001.JPG", StringComparison.Ordinal));
        Assert.Equal(3, plan.Files.Single(f => f.File.Item.RelativePath.EndsWith("DSC_0001.NEF", StringComparison.Ordinal)).Sequence);
        Assert.DoesNotContain(plan.Selected(ImportSelection.OnlyNew), f => f.AlreadyImported);
        Assert.Equal(plan.Files.Count, plan.Selected(ImportSelection.All).Count);
    }

    [Fact]
    public async Task Space_check_flags_a_destination_that_is_too_small()
    {
        var card = _temp.Sub("card2");
        SyntheticFiles.CreateCard(card, 3, 0);
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c2.pfdb"), Path.Combine(_temp.Path, "b2")), Ct);
        var fs = new FixedSpaceFileSystem(new PhysicalFileSystem(), freeBytes: 100);
        var planner = new ImportPlanner(fs, new FakeExifReader(_ => Start), catalog.Assets);
        var plan = await planner.PlanAsync(await ScanAsync(card), "Gara", null, new PathTemplate(PathTemplate.Default), Ct);

        var space = planner.CheckSpace(plan, ImportSelection.OnlyNew, [new("lib", _temp.Path, DestinationKind.Primary), new("nas", @"\\nas\foto", DestinationKind.Deferred)]);

        var check = Assert.Single(space);
        Assert.False(check.IsSufficient);
        Assert.Equal(plan.TotalBytes, check.RequiredBytes);
    }

    private static async Task<List<ScannedFile>> ScanAsync(string root)
    {
        var files = new List<ScannedFile>();
        await foreach (var f in new SourceScanner(new PhysicalFileSystem(), root).ScanAsync(Ct))
        {
            files.Add(f);
        }

        return files;
    }

    private static void Write(string path, byte[] header, int seed)
    {
        var bytes = new byte[4096];
        new Random(seed).NextBytes(bytes);
        header.CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose() => _temp.Dispose();

    private sealed class FixedSpaceFileSystem(IFileSystem inner, long freeBytes) : IFileSystem
    {
        public bool FileExists(string path) => inner.FileExists(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption option) => inner.EnumerateFiles(directory, searchPattern, option);

        public FileMetadata GetFileMetadata(string path) => inner.GetFileMetadata(path);

        public Stream OpenRead(string path) => inner.OpenRead(path);

        public Stream OpenWrite(string path) => inner.OpenWrite(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void Move(string sourcePath, string destinationPath) => inner.Move(sourcePath, destinationPath);

        public void Replace(string replacementPath, string destinationPath, string? backupPath) => inner.Replace(replacementPath, destinationPath, backupPath);

        public long GetAvailableFreeSpace(string path) => freeBytes;
    }
}

[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class DeferredCopyTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-nas");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unreachable_nas_is_retried_later_and_then_verified()
    {
        var (catalog, primary, nas, verified) = await ArrangeAsync();
        await using var _ = catalog;
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var fs = new FaultInjectingFileSystem(new PhysicalFileSystem()) { Unavailable = p => p.StartsWith(nas, StringComparison.OrdinalIgnoreCase) };
        var service = new DeferredCopyService(new CopyEngine(fs), catalog.DeferredCopies, time);
        await service.EnqueueAsync(null, verified, [new IngestDestination("nas", nas, DestinationKind.Deferred)], Ct);

        Assert.Equal(0, await service.ProcessDueAsync(Ct));
        Assert.Equal(1, await catalog.DeferredCopies.CountPendingAsync(Ct));
        Assert.Equal(0, await service.ProcessDueAsync(Ct)); // not due yet: no hammering of the NAS

        fs.Unavailable = _ => false;
        time.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(1, await service.ProcessDueAsync(Ct));
        Assert.Equal(0, await catalog.DeferredCopies.CountPendingAsync(Ct));
        Assert.Equal(FolderSnapshot.Sha256(primary), FolderSnapshot.Sha256(Path.Combine(nas, verified.File.TargetRelativePath)));
    }

    [Fact]
    public async Task A_changed_library_copy_is_never_propagated()
    {
        var (catalog, primary, nas, verified) = await ArrangeAsync();
        await using var _ = catalog;
        var service = new DeferredCopyService(new CopyEngine(new PhysicalFileSystem()), catalog.DeferredCopies);
        await service.EnqueueAsync(null, verified, [new IngestDestination("nas", nas, DestinationKind.Deferred)], Ct);
        await File.WriteAllBytesAsync(primary, [9, 9, 9], Ct); // bit rot or external edit after verification

        Assert.Equal(0, await service.ProcessDueAsync(Ct));

        Assert.Equal(0, await catalog.DeferredCopies.CountPendingAsync(Ct));
        Assert.Empty(Directory.EnumerateFiles(nas, "*.NEF", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(4, 240)]
    [InlineData(20, 1800)]
    public void Retry_delay_grows_and_is_capped(int attempts, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), DeferredCopyService.Delay(attempts));

    private async Task<(SqliteCatalog Catalog, string Primary, string Nas, VerifiedFile Verified)> ArrangeAsync()
    {
        var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var library = _temp.Sub("library", "2026");
        var nas = _temp.Sub("nas");
        var primary = Path.Combine(library, "DSC_0001.NEF");
        var bytes = new byte[100_000];
        new Random(7).NextBytes(bytes);
        await File.WriteAllBytesAsync(primary, bytes, Ct);
        var hash = await CopyEngine.HashAsync(new PhysicalFileSystem(), primary, Ct);
        var verified = new VerifiedFile(new IngestFile(@"E:\DCIM\DSC_0001.NEF", Path.Combine("2026", "DSC_0001.NEF"), bytes.Length), hash, primary, []);
        return (catalog, primary, nas, verified);
    }

    public void Dispose() => _temp.Dispose();
}
