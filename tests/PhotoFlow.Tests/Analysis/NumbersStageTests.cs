// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Search;
using PhotoFlow.Data;
using PhotoFlow.Tagging.Ocr;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>
/// Numbers of a job (spec §8): readings matched with the job's entry list, the doubtful photos of a burst completed by
/// the majority, sponsors ignored, tags written; a different list gives different numbers on the next run; the user's
/// number stays (I5).
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class NumbersStageTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-numbers");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Numbers_come_from_the_list_and_the_burst_majority()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var box = new BoundingBox(0.2f, 0.2f, 0.7f, 0.8f);
        var assets = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            assets.Add(await catalog.Assets.AddAsync(new Asset(0, $"q{i}", null, "JPG", "jpeg", 10, 10, null, DateSource.Unknown, null, AssetState.Active), Ct));
        }

        // Photos 0-3 are a burst of car 9 (one read as 6, one unreadable); photo 4 shows a sponsor's number only.
        string[][] texts = [["9", "PIRELLI"], ["9"], ["6"], [], ["348"]];
        for (var i = 0; i < assets.Count; i++)
        {
            var readings = texts[i].Select(t => new OcrReading(assets[i], 0, box, box, t, t == "6" ? 0.5 : 0.95, "ocr@1")).ToList();
            if (readings.Count == 0)
            {
                readings.Add(new OcrReading(assets[i], 0, box, box, "---", 0.4, "ocr@1"));
            }

            await catalog.Motorsport.ReplaceReadingsAsync(assets[i], readings, Ct);
        }

        var jobId = await catalog.Jobs.CreateAsync(new Job(0, JobType.Import, "src", "Monza", null, "motorsport", false, null, JobState.Completed, JobPriority.Normal, "{}", DateTimeOffset.UtcNow), Ct);
        await catalog.Jobs.AddItemsAsync(jobId, assets.Select((a, i) => new JobItem(0, jobId, a, $"p{i}.jpg", null, null, PipelineStage.Decision, JobItemState.Done, null)).ToList(), Ct);
        await catalog.Groups.ReplaceAutomaticAsync(jobId, [(GroupType.Burst, (IReadOnlyList<long>)assets.Take(4).ToList())], Ct);
        var list = await catalog.Motorsport.CreateListAsync("Monza", null, Ct);
        await catalog.Motorsport.ReplaceEntriesAsync(list, [new Entry(0, list, "9", ["Paolo Galli"], "Galli Racing", null, null), new Entry(0, list, "6", ["Andrea Neri", "Nicolò Bianchi"], "Neri Motorsport", null, null)], Ct);
        await catalog.Motorsport.SetJobListAsync(jobId, list, Ct);

        await new NumbersStage(catalog).RunAsync(new JobContext(jobId, JobType.Import, "motorsport"), null, Ct);

        async Task<VehicleNumber> Main(int i) => (await catalog.Motorsport.GetNumbersAsync(assets[i], Ct)).Single(n => n.Vehicle == 0);
        Assert.Equal(("9", NumberState.Certain, NumberOrigin.Automatic), ((await Main(0)).Number, (await Main(0)).State, (await Main(0)).Origin));
        Assert.Equal(("9", NumberOrigin.Propagated), ((await Main(2)).Number, (await Main(2)).Origin));   // 6 or 9 → the burst says 9
        Assert.Equal(("9", NumberOrigin.Propagated), ((await Main(3)).Number, (await Main(3)).Origin));   // unreadable → 9
        Assert.Equal(NumberState.OutOfList, (await Main(4)).State);                                         // sponsor ignored
        var tags = (await catalog.Tags.GetForAssetAsync(assets[2], Ct)).Select(t => t.Tag.Key).ToList();
        Assert.Contains("number:Numero 9", tags);
        Assert.Contains("driver:Paolo Galli", tags);
        Assert.Empty(await catalog.Tags.GetForAssetAsync(assets[4], Ct));

        // The user says photo 2 is car 6; a new run keeps it.
        await catalog.Motorsport.SetManualNumberAsync(assets[2], 0, "6", Ct);
        await new NumbersStage(catalog).RunAsync(new JobContext(jobId, JobType.Import, "motorsport"), null, Ct);
        Assert.Equal(("6", NumberOrigin.Manual), ((await Main(2)).Number, (await Main(2)).Origin));
        Assert.Contains("number:Numero 6", (await catalog.Tags.GetForAssetAsync(assets[2], Ct)).Select(t => t.Tag.Key));

        // Every name of the list is searchable, also the crew's second driver that the XMP leaves out (D18).
        async Task<long[]> Find(string text) => [.. (await catalog.Search.SearchAsync(SearchQuery.Empty with { Text = text }, Ct)).Hits.Select(h => h.AssetId).Order()];
        Assert.Equal([assets[2]], await Find("Bianchi"));
        Assert.Equal([assets[0], assets[1], assets[3]], await Find("Galli"));
    }

    public void Dispose() => _temp.Dispose();
}
