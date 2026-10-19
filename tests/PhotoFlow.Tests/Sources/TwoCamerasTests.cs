// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Ingest;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Sources;
using PhotoFlow.Data;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Sources.Scanning;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Sources;

/// <summary>
/// Two camera bodies, two cards, one event (M5): the clocks aligned at the import, the numbering going on from the first
/// card, the photos still recognized after the correction; and the card that can be formatted (M6).
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class TwoCamerasTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 6, 20, 15, 0, 0, TimeSpan.FromHours(2));
    private readonly TempFolder _temp = new("pf-two-cameras");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [Trait(TestCategory.Name, TestCategory.Unit)]
    [InlineData("", 0)]
    [InlineData("+5", 5 * 60)]
    [InlineData("-1:30", -(90 * 60))]
    [InlineData("+0:00:45", 45)]
    [InlineData(" - 2 ", -(2 * 60))]
    public void Clock_corrections_are_read_as_typed(string text, int seconds)
    {
        Assert.True(ClockOffset.TryParse(text, out var offset));
        Assert.Equal(TimeSpan.FromSeconds(seconds), offset);
    }

    [Theory]
    [Trait(TestCategory.Name, TestCategory.Unit)]
    [InlineData("cinque")]
    [InlineData("+1:75")]
    [InlineData("12:00:00:00")]
    public void Wrong_corrections_are_refused(string text) => Assert.False(ClockOffset.TryParse(text, out _));

    [Fact]
    public async Task The_clocks_of_two_bodies_give_one_order_and_the_numbering_goes_on()
    {
        var card = _temp.Sub("card");
        var folder = Directory.CreateDirectory(Path.Combine(card, "DCIM", "100NIKON")).FullName;
        // Body A is right; body B is 10 minutes behind: its 15:05 shot was really taken at 15:15.
        var shots = new (string File, string Model, int Minutes)[] { ("A_0001.JPG", "A", 0), ("A_0002.JPG", "A", 10), ("B_0001.JPG", "B", 5), ("A_0003.JPG", "A", 20) };
        foreach (var (file, _, minutes) in shots)
        {
            var bytes = new byte[4096];
            new Random(minutes + file.Length).NextBytes(bytes);
            new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 }.CopyTo(bytes, 0);
            await File.WriteAllBytesAsync(Path.Combine(folder, file), bytes, Ct);
        }

        var exif = new ModelExifReader(p => shots.Single(s => s.File == Path.GetFileName(p)) is var s ? (s.Model, Start.AddMinutes(s.Minutes)) : default);
        var fs = new PhysicalFileSystem();
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var scanned = new List<ScannedFile>();
        await foreach (var f in new SourceScanner(fs, card).ScanAsync(Ct))
        {
            scanned.Add(f);
        }

        var planner = new ImportPlanner(fs, exif, catalog.Assets);
        var plan = await planner.PlanAsync(scanned, "Matrimonio", null, new PathTemplate("{evento}/{seq:0000}.{ext}"), Ct,
            new Dictionary<string, TimeSpan> { ["B"] = TimeSpan.FromMinutes(10) }, sequenceStart: 120);

        Assert.Equal(["A", "B"], plan.Cameras.Select(c => c.Model).Order());
        string Target(string file) => plan.Files.Single(f => f.File.Item.RelativePath.EndsWith(file, StringComparison.Ordinal)).TargetRelativePath.Replace('\\', '/');
        Assert.Equal("Matrimonio/0121.JPG", Target("A_0001.JPG"));
        Assert.Equal("Matrimonio/0122.JPG", Target("A_0002.JPG"));
        Assert.Equal("Matrimonio/0123.JPG", Target("B_0001.JPG")); // 15:05 + 10 minutes: after A_0002
        Assert.Equal("Matrimonio/0124.JPG", Target("A_0003.JPG"));
        Assert.Equal(124, plan.LastSequence);

        // In the catalog the corrected time orders the shots; the camera's time still recognizes the photo.
        var quick = await QuickHash.ComputeAsync(fs, Path.Combine(folder, "B_0001.JPG"), Ct);
        var id = await catalog.Assets.AddAsync(new Asset(0, quick, null, "JPG", "jpeg", null, null, Start.AddMinutes(5), DateSource.Exif, null, AssetState.Active), Ct);
        await catalog.Assets.SetClockOffsetAsync(id, TimeSpan.FromMinutes(10), Ct);
        Assert.Equal(Start.AddMinutes(15), (await catalog.Assets.GetAsync(id, Ct))!.CapturedAt);
        Assert.Equal(id, (await catalog.Assets.FindByIdentityAsync(quick, Start.AddMinutes(5), Ct))?.Id);
        var again = await planner.PlanAsync(scanned, "Matrimonio", null, new PathTemplate("{evento}/{seq:0000}.{ext}"), Ct);
        Assert.True(again.Files.Single(f => f.File.Item.RelativePath.EndsWith("B_0001.JPG", StringComparison.Ordinal)).AlreadyImported);
    }

    [Fact]
    public async Task A_card_is_safe_with_two_verified_copies_only()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "s.pfdb"), Path.Combine(_temp.Path, "sb")), Ct);
        var card = new CardVolume(@"F:\", "NIKON Z5", 64_000_000_000);
        var plan = new ImportPlan([], 0, 12, 0, 0, 0, null, null, new DateOnly(2026, 6, 20));
        async Task<long> ImportAsync(IReadOnlyList<IngestDestination> destinations, JobState state, bool failed = false)
        {
            var settings = new ImportJobSettings(1, @"F:\", destinations, "events");
            var jobId = await catalog.Jobs.CreateAsync(new Job(0, JobType.Import, @"F:\", "Matrimonio", null, "events", false, null, JobState.Pending, JobPriority.Normal,
                ImportJobRunner.SerializeSettings(settings), DateTimeOffset.UtcNow), Ct);
            await catalog.Jobs.AddItemsAsync(jobId, [new JobItem(0, jobId, null, @"F:\DCIM\A.JPG", "A.JPG", null, PipelineStage.None, failed ? JobItemState.Failed : JobItemState.Done, null)], Ct);
            await catalog.Jobs.SetStateAsync(jobId, state, Ct);
            return jobId;
        }

        Assert.Equal(CardSafetyState.None, (await CardSafety.AssessAsync(catalog, card, plan with { NewPhotos = 3 }, Ct)).State);

        await ImportAsync([new("p", @"D:\Foto", DestinationKind.Primary)], JobState.Completed);
        Assert.Equal(CardSafetyState.OneCopy, (await CardSafety.AssessAsync(catalog, card, plan, Ct)).State);

        await ImportAsync([new("p", @"D:\Foto", DestinationKind.Primary), new("s", @"E:\Backup", DestinationKind.Secondary)], JobState.Completed);
        var safe = await CardSafety.AssessAsync(catalog, card, plan, Ct);
        Assert.Equal(CardSafetyState.Safe, safe.State);
        Assert.Contains(@"E:\Backup", safe.Text, StringComparison.Ordinal);

        await ImportAsync([new("p", @"D:\Foto", DestinationKind.Primary), new("s", @"E:\Backup", DestinationKind.Secondary)], JobState.Completed, failed: true);
        Assert.Equal(CardSafetyState.Failed, (await CardSafety.AssessAsync(catalog, card, plan, Ct)).State);

        await ImportAsync([new("p", @"D:\Foto", DestinationKind.Primary), new("s", @"E:\Backup", DestinationKind.Secondary)], JobState.Running);
        Assert.Equal(CardSafetyState.Importing, (await CardSafety.AssessAsync(catalog, card, plan, Ct)).State);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>EXIF with a camera model and a capture time per file.</summary>
    private sealed class ModelExifReader(Func<string, (string Model, DateTimeOffset At)> shot) : IExifReader
    {
        public Task<IReadOnlyList<ExifInfo>> ReadAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExifInfo>>(paths.Select(p =>
            {
                var (model, at) = shot(p);
                return new ExifInfo(p, "JPEG", "NIKON", model, null, 100, 0.001, 2.8, 50, 0, at, DateSource.Exif, 1, null, 6048, 4032, null, null, null);
            }).ToList());
    }
}
