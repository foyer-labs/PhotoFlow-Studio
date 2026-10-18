// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Tagging;
using PhotoFlow.Data;
using PhotoFlow.Tagging.Vocabulary;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>
/// Tagging batch stage (spec §8): the event becomes a tag of every photo; personal CLIP tags (level A) from the stored
/// embeddings, with threshold and margin over the second candidate; a tag the user removed is not added again; the
/// level chosen for the job is respected. The shipped vocabulary has every RAM++ tag with an Italian label.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class TaggingStageTests : IDisposable
{
    private const string Clip = "test-clip@1";
    private readonly TempFolder _temp = new("pf-tagging");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Event_and_personal_tags_are_assigned_and_a_removed_tag_stays_removed()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);

        // Photo 1 looks like the lake, photo 2 like the mountain, photo 3 like both (no clear winner).
        var lake = Axis(1);
        var mountain = Axis(2);
        var both = Vectors.Normalize([0, 1, 1, 0]);
        var assets = new List<long>();
        foreach (var vector in new[] { lake, mountain, both })
        {
            var id = await catalog.Assets.AddAsync(new Asset(0, $"q{assets.Count}", $"f{assets.Count}", "JPG", "jpeg", 10, 10, null, DateSource.Unknown, null, AssetState.Active), Ct);
            await catalog.Analysis.SaveEmbeddingAsync(id, Clip, vector, Ct);
            assets.Add(id);
        }

        var jobId = await JobAsync(catalog, assets, "Gita al lago", TagLevels.Default);
        await catalog.Tags.SaveCustomAsync("custom:lake-como", "Lago di Como", "Luoghi", "a photo of Lake Como", 0.5, 0.1, Ct);
        await catalog.Tags.SaveCustomAsync("custom:mountain", "Montagna", "Natura", "a photo of a mountain", 0.5, 0.1, Ct);
        var scan = new CustomTagScan(catalog, new CustomTagClassifier(new AxisText()), Clip);
        var stage = new TaggingStage(catalog, _ => Task.FromResult<CustomTagScan?>(scan));

        await stage.RunAsync(new JobContext(jobId, JobType.Import, "events"), null, Ct);

        async Task<string[]> Active(long id) => (await catalog.Tags.GetForAssetAsync(id, Ct)).Where(t => t.Assignment.State == AssetTagState.Active).Select(t => t.Tag.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["custom:lake-como", "event:Gita al lago"], await Active(assets[0]));
        Assert.Equal(["custom:mountain", "event:Gita al lago"], await Active(assets[1]));
        Assert.Equal(["event:Gita al lago"], await Active(assets[2])); // above the threshold twice, no margin
        var eventTag = (await catalog.Tags.FindAsync("event:Gita al lago", Ct))!;
        Assert.Equal(("Eventi", true, "Gita al lago"), (eventTag.Category, eventTag.Reviewed, eventTag.Italian));

        // The user removes the lake from photo 1: running again does not add it back.
        await catalog.Tags.SuppressAsync(assets[0], (await catalog.Tags.FindAsync("custom:lake-como", Ct))!.Id, Ct);
        await stage.RunAsync(new JobContext(jobId, JobType.Import, "events"), null, Ct);
        Assert.Equal(["event:Gita al lago"], await Active(assets[0]));

        // A trial counts without saving.
        var (lakeTag, lakeCustom) = (await catalog.Tags.ListCustomAsync(Ct)).Single(t => t.Tag.Key == "custom:lake-como");
        Assert.Equal(1, await scan.TrialAsync(lakeTag, lakeCustom with { Margin = 0.0, Threshold = 0.9 }, Ct));

        // A job without level A gets only the event.
        var other = await catalog.Assets.AddAsync(new Asset(0, "q9", "f9", "JPG", "jpeg", 10, 10, null, DateSource.Unknown, null, AssetState.Active), Ct);
        await catalog.Analysis.SaveEmbeddingAsync(other, Clip, lake, Ct);
        var noClip = await JobAsync(catalog, [other], "Senza A", TagLevels.Tagger);
        await stage.RunAsync(new JobContext(noClip, JobType.Import, "events"), null, Ct);
        Assert.Equal(["event:Senza A"], await Active(other));
    }

    [Fact]
    [Trait(TestCategory.Name, TestCategory.Unit)]
    public void The_vocabulary_has_every_ramplus_tag_with_an_italian_label_and_a_known_category()
    {
        var entries = RamPlusVocabulary.Entries;
        Assert.Equal(4585, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Italian), e.Key));
        Assert.All(entries.Where(e => e.Category is not null), e => Assert.Contains(e.Category, (string[])["Persone", "Luoghi", "Natura", "Veicoli", "Eventi", "Animali"]));
        Assert.Equal("tramonto", entries.Single(e => e.Key == "sunset").Italian);
        Assert.Equal("Natura", entries.Single(e => e.Key == "sunset").Category);
        Assert.True(entries.Single(e => e.Key == "photo").Blacklisted);
        Assert.Equal(7, entries.Count(e => e.Blacklisted));
    }

    public void Dispose() => _temp.Dispose();

    private static float[] Axis(int i)
    {
        var v = new float[4];
        v[i] = 1;
        return v;
    }

    private static async Task<long> JobAsync(SqliteCatalog catalog, IReadOnlyList<long> assets, string eventName, TagLevels tags)
    {
        var jobId = await catalog.Jobs.CreateAsync(new Job(0, JobType.Import, "src", eventName, null, "events", false, null, JobState.Completed, JobPriority.Normal,
            $"{{\"tags\":{(int)tags}}}", DateTimeOffset.UtcNow), Ct);
        await catalog.Jobs.AddItemsAsync(jobId, assets.Select((a, i) => new JobItem(0, jobId, a, $"p{a}-{i}.jpg", null, null, PipelineStage.Decision, JobItemState.Done, null)).ToList(), Ct);
        return jobId;
    }

    /// <summary>"lake" → axis 1, "mountain" → axis 2.</summary>
    private sealed class AxisText : ITextEmbedder
    {
        public string ModelVersion => "axis@1";

        public Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
            Task.FromResult(Axis(text.Contains("Lake", StringComparison.Ordinal) ? 1 : text.Contains("mountain", StringComparison.Ordinal) ? 2 : 3));
    }
}
