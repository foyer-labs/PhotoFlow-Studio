// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Data;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Data;

[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class CatalogTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-cat");

    private CatalogOpenOptions Options => new(Path.Combine(_temp.Path, "catalog.pfdb"), Path.Combine(_temp.Path, "backups"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_catalog_gets_the_latest_schema_without_backup()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(Options, Ct);

        Assert.Equal(CatalogMigrator.Embedded().Max(m => m.Version), await catalog.GetSchemaVersionAsync(Ct));
        Assert.False(Directory.Exists(Options.BackupsDirectory) && Directory.EnumerateFiles(Options.BackupsDirectory).Any());
    }

    [Fact]
    public async Task A_pending_migration_is_preceded_by_a_backup_of_the_previous_catalog()
    {
        var v1 = new CatalogMigration(1, "0001_test.sql", "CREATE TABLE A (Id INTEGER PRIMARY KEY);");
        var v2 = new CatalogMigration(2, "0002_test.sql", "CREATE TABLE B (Id INTEGER PRIMARY KEY);");
        await CatalogMigrator.ApplyAsync(Options, [v1], Ct);

        var applied = await CatalogMigrator.ApplyAsync(Options, [v1, v2], Ct);

        Assert.Equal([2], applied);
        var backup = Assert.Single(Directory.GetFiles(Options.BackupsDirectory));
        Assert.Contains("v1", Path.GetFileName(backup), StringComparison.Ordinal);
        Assert.Equal(1, await CatalogMigrator.ReadVersionAsync(backup, Ct));
        Assert.Equal(2, await CatalogMigrator.ReadVersionAsync(Options.CatalogPath, Ct));
    }

    [Fact]
    public async Task Only_one_process_at_a_time_can_open_the_catalog()
    {
        await using (await SqliteCatalog.OpenAsync(Options, Ct))
        {
            await Assert.ThrowsAsync<CatalogLockedException>(() => SqliteCatalog.OpenAsync(Options, Ct));
        }

        await using var reopened = await SqliteCatalog.OpenAsync(Options, Ct);
        Assert.NotNull(reopened);
    }

    [Fact]
    public async Task A_corrupted_catalog_is_reported_instead_of_being_used()
    {
        await File.WriteAllBytesAsync(Options.CatalogPath, Enumerable.Repeat((byte)0x5A, 8192).ToArray(), Ct);

        await Assert.ThrowsAsync<CatalogCorruptException>(() => SqliteCatalog.OpenAsync(Options, Ct));
    }

    [Theory]
    [InlineData(@"\\nas\foto\catalog.pfdb", "DELETE")]
    [InlineData("C:/Users/x/catalog.pfdb", "WAL")]
    public void Network_catalogs_use_the_rollback_journal(string path, string expected) =>
        Assert.Equal(expected, CatalogJournal.ModeFor(path));

    [Fact]
    public async Task Home_queries_count_outcomes_photos_and_recent_jobs()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(Options, Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "Foto", @"D:\Foto", WritePolicy.EmbeddedForNonRaw, false, LibraryOrigin.Import, null), Ct);
        Assert.Equal(libraryId, (await catalog.Libraries.FindByRootAsync(@"d:\foto", Ct))?.Id);
        var jobId = await catalog.Jobs.CreateAsync(new Job(0, JobType.Import, "E:", "A", null, "events", false, null, JobState.Completed, JobPriority.Normal, "{}", DateTimeOffset.UtcNow), Ct);
        var later = await catalog.Jobs.CreateAsync(new Job(0, JobType.Import, "E:", "B", null, "events", false, null, JobState.Pending, JobPriority.Normal, "{}", DateTimeOffset.UtcNow.AddMinutes(1)), Ct);
        var items = new List<JobItem>();
        foreach (var (name, outcome) in new[] { ("a", CullingOutcome.Kept), ("b", CullingOutcome.ToReview), ("c", CullingOutcome.ToReview) })
        {
            var assetId = await catalog.Assets.AddAsync(new Asset(0, name, null, "NEF", "raw", null, null, null, DateSource.Unknown, null, AssetState.Active), Ct);
            await catalog.Assets.AddLocationAsync(new AssetLocation(0, assetId, libraryId, name + ".NEF", DateTimeOffset.UtcNow), Ct);
            await catalog.Decisions.SetAutomaticAsync(assetId, outcome, [], "v", Ct);
            items.Add(new JobItem(0, jobId, assetId, name, name, null, PipelineStage.Decision, JobItemState.Done, null));
        }

        await catalog.Jobs.AddItemsAsync(jobId, items, Ct);
        await catalog.Decisions.SetUserAsync(items[2].AssetId!.Value, CullingOutcome.Kept, null, null, Ct); // the user's decision counts

        var counts = await catalog.Decisions.CountOutcomesForJobAsync(jobId, Ct);
        Assert.Equal(2, counts[CullingOutcome.Kept]);
        Assert.Equal(1, counts[CullingOutcome.ToReview]);
        Assert.Equal(3, await catalog.Libraries.CountAssetsAsync(libraryId, Ct));
        Assert.Equal([later, jobId], (await catalog.Jobs.ListRecentAsync(5, Ct)).Select(j => j.Id));
    }

    [Fact]
    public async Task Stores_round_trip_the_phase_1_entities()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(Options, Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "Principale", @"D:\Foto", WritePolicy.EmbeddedForNonRaw, false, LibraryOrigin.Import, null), Ct);
        var captured = new DateTimeOffset(2026, 10, 12, 10, 2, 3, 450, TimeSpan.FromHours(2));
        var exif = new ExifSummary("NIKON Z5_2", "Z 85mm f/1.8 S", 1600, 1.0 / 1000, 2.8, 85, -0.3, "Center", new BoundingBox(0.1f, 0.2f, 0.3f, 1f / 3));
        var assetId = await catalog.Assets.AddAsync(new Asset(0, "q1", "f1", "NEF", "raw-preview", 6048, 4032, captured, DateSource.Exif, exif, AssetState.Active), Ct);
        await catalog.Assets.AddLocationAsync(new AssetLocation(0, assetId, libraryId, "2026/a.NEF", captured), Ct);

        Assert.Equal("Principale", Assert.Single(await catalog.Libraries.ListAsync(Ct)).Name);
        var found = await catalog.Assets.FindByIdentityAsync("q1", captured, Ct);
        Assert.Equal(assetId, found?.Id);
        Assert.Equal(exif, found?.Exif);
        Assert.Equal(captured, found?.CapturedAt);
        Assert.Null(await catalog.Assets.FindByIdentityAsync("q1", captured.AddSeconds(1), Ct));
        Assert.Equal(assetId, (await catalog.Assets.FindByFullHashAsync("f1", Ct))?.Id);
        Assert.Equal("2026/a.NEF", Assert.Single(await catalog.Assets.GetLocationsAsync(assetId, Ct)).RelativePath);

        var jobId = await catalog.Jobs.CreateAsync(new Job(0, JobType.Import, "E:", "Matrimonio", new DateOnly(2026, 10, 12), "eventi", false, null, JobState.Running, JobPriority.Normal, "{}", captured), Ct);
        await catalog.Jobs.AddItemsAsync(jobId, [new JobItem(0, jobId, null, @"E:\DCIM\a.NEF", "2026/a.NEF", null, PipelineStage.None, JobItemState.Pending, null)], Ct);
        var item = Assert.Single(await catalog.Jobs.GetItemsAsync(jobId, Ct));
        await catalog.Jobs.UpdateItemAsync(item with { AssetId = assetId, ContentHash = "f1", LastCompletedStage = PipelineStage.CopyAndVerify, State = JobItemState.InProgress }, Ct);
        Assert.Equal(PipelineStage.CopyAndVerify, Assert.Single(await catalog.Jobs.GetItemsAsync(jobId, Ct)).LastCompletedStage);
        Assert.Equal(jobId, Assert.Single(await catalog.Jobs.ListUnfinishedAsync(Ct)).Id);
        Assert.Equal(new DateOnly(2026, 10, 12), (await catalog.Jobs.GetAsync(jobId, Ct))?.EventDate);

        await catalog.XmpSync.MarkPendingAsync(assetId, "access denied", Ct);
        Assert.Equal([assetId], await catalog.XmpSync.ListPendingAsync(Ct));
        await catalog.XmpSync.MarkWrittenAsync(assetId, "sig", Ct);
        Assert.Empty(await catalog.XmpSync.ListPendingAsync(Ct));
    }

    public void Dispose() => _temp.Dispose();
}
