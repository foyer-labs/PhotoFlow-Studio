// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Tagging;
using PhotoFlow.Data;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Tagging;

/// <summary>
/// Level D in a pass of its own (phase 5b, P16): a job that asked for it gets an Italian caption for each photo after
/// the analysis, in one exclusive pass; the user's Italian caption is never replaced (I5) and the English one stays;
/// a resumed or repeated job captions only what is missing; a job without level D, or a PC that cannot run it, does
/// nothing. The regeneration does level D in the same way and stages the captions with the others, without analyzing
/// again the photos whose only stale part is the Italian caption.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class VlmCaptionStageTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-vlm-stage");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_job_with_level_D_gets_Italian_captions_in_one_pass()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), _temp.Sub("b")), Ct);
        var ids = await AddPhotosAsync(catalog, 3);
        await catalog.Captions.SetAutomaticAsync(ids[0], "en", "A lake.", "florence@1", Ct);
        await catalog.Captions.SetManualAsync(ids[1], "it", "Il lago di Sara.", Ct);
        var vlm = new FakeVlm("qwen@1");
        var stage = new VlmCaptionStage(catalog, new PhysicalFileSystem(), new FakePreviews(), vlm);

        // Without level D: nothing.
        var plain = await JobAsync(catalog, ids, TagLevels.Default | TagLevels.Caption);
        await stage.RunAsync(new JobContext(plain, JobType.AnalyzeInPlace, "events"), null, Ct);
        Assert.Equal(0, vlm.Passes);

        // With level D: one pass, the user's caption kept, the English one still there.
        var job = await JobAsync(catalog, ids, TagLevels.Default | TagLevels.VlmCaption);
        await stage.RunAsync(new JobContext(job, JobType.AnalyzeInPlace, "events"), null, Ct);
        Assert.Equal((1, 2), (vlm.Passes, vlm.Captions));
        Assert.Equal([("it", "Didascalia 1", CaptionOrigin.Automatic), ("en", "A lake.", CaptionOrigin.Automatic)],
            (await catalog.Captions.GetAllAsync(ids[0], Ct)).Select(c => (c.Language, c.Text, c.Origin)));
        Assert.Equal(("Il lago di Sara.", CaptionOrigin.Manual), (await catalog.Captions.GetAsync(ids[1], Ct)) is { } c ? (c.Text, c.Origin) : default);
        Assert.Equal("qwen@1", (await catalog.Captions.GetAsync(ids[2], Ct))!.ModelVersion);

        // Again (a resumed job): nothing to do, no pass.
        await stage.RunAsync(new JobContext(job, JobType.AnalyzeInPlace, "events"), null, Ct);
        Assert.Equal(1, vlm.Passes);

        // A newer model: the automatic captions again, never the user's.
        vlm.Version = "qwen@2";
        await stage.RunAsync(new JobContext(job, JobType.AnalyzeInPlace, "events"), null, Ct);
        Assert.Equal((2, 4), (vlm.Passes, vlm.Captions));
        Assert.Equal("Il lago di Sara.", (await catalog.Captions.GetAsync(ids[1], Ct))!.Text);

        // A PC that cannot run level D.
        vlm.Version = null;
        await stage.RunAsync(new JobContext(job, JobType.AnalyzeInPlace, "events"), null, Ct);
        Assert.Equal(2, vlm.Passes);
    }

    [Fact]
    public async Task A_model_that_does_not_run_stops_the_pass_without_failing_the_job()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), _temp.Sub("b")), Ct);
        var ids = await AddPhotosAsync(catalog, 6);
        var vlm = new FakeVlm("qwen@1") { Fail = true };
        var job = await JobAsync(catalog, ids, TagLevels.VlmCaption);
        await new VlmCaptionStage(catalog, new PhysicalFileSystem(), new FakePreviews(), vlm).RunAsync(new JobContext(job, JobType.AnalyzeInPlace, "events"), null, Ct);
        Assert.Equal(VlmCaptionStage.MaxConsecutiveFailures, vlm.Attempts);
        foreach (var id in ids)
        {
            Assert.Null(await catalog.Captions.GetAsync(id, Ct));
        }
    }

    [Fact]
    public async Task The_regeneration_stages_level_D_without_analyzing_again()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), _temp.Sub("b")), Ct);
        var ids = await AddPhotosAsync(catalog, 2);
        foreach (var id in ids)
        {
            await catalog.Captions.SetAutomaticAsync(id, "en", "A lake.", "florence@2", Ct);
            await catalog.Captions.SetAutomaticAsync(id, "it", "Un lago.", "qwen@1", Ct);
        }

        await catalog.Captions.SetManualAsync(ids[1], "it", "Il lago di Sara.", Ct);
        var current = new ModelVersions("culling-v1", null, null, null, null, "florence@2", null, "qwen@2");
        var processor = new CountingProcessor();
        var vlm = new FakeVlm("qwen@2");
        var runner = new ReanalysisJobRunner(catalog, new PhysicalFileSystem(), processor, _ => Task.FromResult(current), vlm, new FakePreviews());
        var jobId = await ReanalysisJobRunner.CreateAsync(catalog, new ReanalysisSettings(ReanalysisParts.Captions, AssetIds: ids), Ct);
        await runner.RunAsync(jobId, null, Ct);

        // Only the Italian caption of photo 0 was older: no analysis, one pass, one caption staged.
        Assert.Equal(0, processor.Calls);
        Assert.Equal((1, 1), (vlm.Passes, vlm.Captions));
        var staged = await catalog.Reanalysis.ListAsync(jobId, Ct);
        Assert.Equal([ids[0]], staged.Select(s => s.AssetId));
        Assert.Equal("qwen@2", JsonSerializer.Deserialize<List<GeneratedCaption>>(staged[0].PayloadJson, ReanalysisJobRunner.Json)!.Single().ModelVersion);

        await Reanalysis.ApplyAsync(catalog, jobId, () => PhotoFlow.Faces.Protection.UnavailableProtector.Instance, null, false, Ct);
        Assert.Equal([("it", "Didascalia 1"), ("en", "A lake.")], (await catalog.Captions.GetAllAsync(ids[0], Ct)).Select(c => (c.Language, c.Text)));
        Assert.Equal("Il lago di Sara.", (await catalog.Captions.GetAsync(ids[1], Ct))!.Text);
    }

    public void Dispose() => _temp.Dispose();

    private async Task<List<long>> AddPhotosAsync(SqliteCatalog catalog, int count)
    {
        var root = _temp.Sub("library");
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);
        var ids = new List<long>();
        for (var i = 0; i < count; i++)
        {
            SyntheticImages.WriteJpeg(Path.Combine(root, $"p{i}.jpg"), 64, 48);
            var id = await catalog.Assets.AddAsync(new Asset(0, $"q{i}", $"f{i}", "JPG", "jpeg", 64, 48, null, DateSource.Unknown, null, AssetState.Active), Ct);
            await catalog.Assets.AddLocationAsync(new AssetLocation(0, id, libraryId, $"p{i}.jpg", DateTimeOffset.UtcNow), Ct);
            ids.Add(id);
        }

        return ids;
    }

    private static async Task<long> JobAsync(SqliteCatalog catalog, IReadOnlyList<long> ids, TagLevels tags)
    {
        var jobId = await catalog.Jobs.CreateAsync(new Job(0, JobType.AnalyzeInPlace, null, null, null, "events", false, null, JobState.Completed, JobPriority.Low,
            JsonSerializer.Serialize(new { tags = (int)tags }), DateTimeOffset.UtcNow), Ct);
        await catalog.Jobs.AddItemsAsync(jobId, ids.Select(id => new JobItem(0, jobId, id, $"#{id}", null, null, PipelineStage.Decision, JobItemState.Done, null)).ToList(), Ct);
        return jobId;
    }

    private sealed class FakeVlm(string? version) : IExclusiveCaptioning, IImageCaptioner
    {
        public string? Version { get; set; } = version;

        public int Passes { get; private set; }

        public int Captions { get; private set; }

        public bool Fail { get; init; }

        public int Attempts { get; private set; }

        string? IExclusiveCaptioning.ModelVersion => Version;

        string IImageCaptioner.ModelVersion => Version!;

        public string Language => "it";

        public async Task RunAsync(Func<IImageCaptioner, Task> work, CancellationToken ct)
        {
            Passes++;
            await work(this);
        }

        public Task<string> CaptionAsync(RgbImage image, CancellationToken ct)
        {
            Attempts++;
            return Fail ? throw new InvalidOperationException("out of memory") : Task.FromResult($"Didascalia {++Captions}");
        }
    }

    private sealed class FakePreviews : IPreviewProvider
    {
        public Task<PreviewImage> GetPreviewAsync(string path, int maxSize, CancellationToken ct) =>
            Task.FromResult(new PreviewImage(new RgbImage(4, 3, new byte[4 * 3 * 3]), 4, 3, false));
    }

    private sealed class CountingProcessor : IPhotoProcessor
    {
        public int Calls { get; private set; }

        public Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Not expected: only level D is stale.");
        }
    }
}
