// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Tagging;
using PhotoFlow.Data;
using PhotoFlow.Faces.Protection;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>
/// Regeneration (spec §5, D14) with a processor standing for the new models: only the results of older models are
/// computed again, they wait in the staging area (the catalog does not change before the confirmation), the preview
/// counts the differences, the confirmation applies them keeping what the user decided (I5): decisions, manual and
/// suppressed tags, the user's caption, the user's face assignments; a second regeneration finds nothing to do; a
/// trial on a sample takes only that many photos; discarding leaves the catalog as it was.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class ReanalysisTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-reanalysis");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_models_results_wait_for_the_preview_and_keep_what_the_user_decided()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var root = _temp.Sub("library");
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);
        var keys = new FingerprintCipher(RandomNumberGenerator.GetBytes(32));
        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            SyntheticImages.WriteJpeg(Path.Combine(root, $"p{i}.jpg"), 300, 200);
            var id = await catalog.Assets.AddAsync(new Asset(0, $"q{i}", $"f{i}", "JPG", "jpeg", 300, 200, null, DateSource.Unknown, null, AssetState.Active), Ct);
            await catalog.Assets.AddLocationAsync(new AssetLocation(0, id, libraryId, $"p{i}.jpg", DateTimeOffset.UtcNow), Ct);
            await catalog.Analysis.SaveResultAsync(new AnalyzerResultRecord(0, id, "culling-v1", "culling-v1+det@1", "events", new Dictionary<string, double> { ["sharpness"] = 0.5 }), Ct);
            await catalog.Decisions.SetAutomaticAsync(id, CullingOutcome.Kept, [], "engine+culling-v1+det@1", Ct);
            await catalog.Tags.ReplaceAutomaticAsync(id, TagSource.RamPlus, "ram@1", [("lake", 0.9), ("boat", 0.8)], Ct);
            await catalog.Captions.SetAutomaticAsync(id, "en", "A lake.", "florence@1", Ct);
            await catalog.Analysis.ReplaceAutomaticFacesAsync(id, [new Face(0, id, new BoundingBox(0.4f, 0.2f, 0.5f, 0.35f), null, 0.9, EyeState.Open, EyeState.Open,
                keys.Protect([1f, 0f, 0f]), null, null, null, "culling-v1+det@1", [new Point2(0.4f, 0.2f)], "arc@1/arcface")], Ct);
            ids.Add(id);
        }

        // What the user decided on photo 0: discard, a manual tag, "boat" removed, a caption, the face is Anna.
        await catalog.Decisions.SetUserAsync(ids[0], CullingOutcome.Discard, null, null, Ct);
        await catalog.Tags.AddManualAsync(ids[0], "vacanze", Ct);
        await catalog.Tags.SuppressAsync(ids[0], (await catalog.Tags.FindAsync("boat", Ct))!.Id, Ct);
        await catalog.Captions.SetManualAsync(ids[0], "en", "Lago di Como, 2026.", Ct);
        var anna = await catalog.People.CreatePersonAsync("Anna", Ct);
        await catalog.People.AssignManualAsync([(await catalog.Analysis.GetFacesAsync(ids[0], Ct)).Single().Id], anna, Ct);

        var current = new ModelVersions("culling-v1", "culling-v1+det@2", "clip@1", "sface@1/sface", "ram@2", "florence@2", null);
        var processor = new NewModels(keys);
        var runner = new ReanalysisJobRunner(catalog, new PhysicalFileSystem(), processor, _ => Task.FromResult(current));

        // A trial on one photo.
        var trial = await ReanalysisJobRunner.CreateAsync(catalog, new ReanalysisSettings(ReanalysisParts.All, LibraryId: libraryId, Sample: 1), Ct);
        await runner.RunAsync(trial, null, Ct);
        Assert.Equal(1, await catalog.Reanalysis.CountAsync(trial, Ct));
        await Reanalysis.DiscardAsync(catalog, trial, Ct);

        // The whole library: staged only.
        var jobId = await ReanalysisJobRunner.CreateAsync(catalog, new ReanalysisSettings(ReanalysisParts.All, LibraryId: libraryId), Ct);
        await runner.RunAsync(jobId, null, Ct);
        Assert.Equal(3, processor.Calls - 1);
        Assert.Equal(3, await catalog.Reanalysis.CountAsync(jobId, Ct));
        Assert.Equal("ram@1", (await catalog.Tags.GetForAssetAsync(ids[1], Ct)).First(t => t.Tag.Key == "lake").Assignment.ModelVersion);  // nothing changed yet

        var preview = await Reanalysis.PreviewAsync(catalog, jobId, Ct);
        Assert.Equal(3, preview.Photos);
        Assert.Equal((3, 1), (preview.OutcomesChanged, preview.UserDecisionsKept));        // Kept → ToReview everywhere; photo 0 keeps Discard
        Assert.Equal((3, 2, 1), (preview.TagsAdded, preview.TagsRemoved, preview.TagsProtected)); // +sunset ×3, −boat ×2, boat stays suppressed on 0
        Assert.Equal((0, 2, 1), (preview.CaptionsProtected, preview.CaptionsChanged, preview.FacesTransferred)); // the user's caption is not even computed again
        Assert.NotEmpty(preview.Examples);

        var applied = await Reanalysis.ApplyAsync(catalog, jobId, () => keys, null, true, Ct);
        Assert.Equal((3, 1), (applied.Photos, applied.Transferred));
        Assert.Equal(0, await catalog.Reanalysis.CountAsync(jobId, Ct));

        // Photo 0: the user's decisions stand.
        var decision = (await catalog.Decisions.GetAsync(ids[0], Ct))!;
        Assert.Equal((CullingOutcome.Discard, CullingOutcome.ToReview), (decision.UserOutcome!.Value, decision.AutomaticOutcome!.Value));
        var tags = await catalog.Tags.GetForAssetAsync(ids[0], Ct);
        Assert.Equal(AssetTagState.Suppressed, tags.Single(t => t.Tag.Key == "boat").Assignment.State);
        Assert.Equal(AssetTagState.Active, tags.Single(t => t.Tag.Key == "vacanze").Assignment.State);
        Assert.Equal("ram@2", tags.Single(t => t.Tag.Key == "sunset").Assignment.ModelVersion);
        Assert.Equal("Lago di Como, 2026.", (await catalog.Captions.GetAsync(ids[0], Ct))!.Text);
        var face = (await catalog.Analysis.GetFacesAsync(ids[0], Ct)).Single();
        Assert.Equal((anna, AssignmentOrigin.Manual, "sface@1/sface"), (face.PersonId!.Value, face.AssignmentOrigin!.Value, face.EmbeddingModel));

        // Photo 1: everything from the new models.
        Assert.DoesNotContain(await catalog.Tags.GetForAssetAsync(ids[1], Ct), t => t.Tag.Key == "boat" && t.Assignment.State == AssetTagState.Active);
        Assert.Equal("A lake at sunset.", (await catalog.Captions.GetAsync(ids[1], Ct))!.Text);
        Assert.Equal("culling-v1+det@2", (await catalog.Analysis.GetResultsAsync([ids[1]], "culling-v1", Ct))[ids[1]].ModelVersion);
        Assert.Single(await catalog.Analysis.GetEmbeddingsAsync([ids[1]], "clip@1", Ct));

        // Nothing is older now: a new regeneration has nothing to compute.
        var again = await ReanalysisJobRunner.CreateAsync(catalog, new ReanalysisSettings(ReanalysisParts.All, LibraryId: libraryId), Ct);
        var calls = processor.Calls;
        await runner.RunAsync(again, null, Ct);
        Assert.Equal(calls, processor.Calls);
        Assert.Equal(0, await catalog.Reanalysis.CountAsync(again, Ct));
    }

    [Fact]
    public void Each_caption_is_compared_with_the_model_of_its_language()
    {
        Caption Auto(string language, string version) => new(1, language, "x", version, CaptionOrigin.Automatic, DateTimeOffset.UtcNow);
        var both = new ModelVersions("culling-v1", null, null, null, null, "florence@2", null, "qwen@1");
        var noVlm = both with { VlmCaptions = null };

        // Florence older, Italian current: only level C again; the reverse: only level D.
        Assert.Equal((true, false), both.CaptionsToRedo([Auto("en", "florence@1"), Auto("it", "qwen@1")], forceAll: false));
        Assert.Equal((false, true), both.CaptionsToRedo([Auto("en", "florence@2"), Auto("it", "qwen@0")], forceAll: false));

        // The user's caption and a missing language are not stale; without the VLM its captions stay as they are.
        Assert.Equal((false, false), both.CaptionsToRedo([new Caption(1, "it", "Mia", null, CaptionOrigin.Manual, DateTimeOffset.UtcNow), Auto("en", "florence@2")], forceAll: false));
        Assert.Equal((false, false), noVlm.CaptionsToRedo([Auto("en", "florence@2"), Auto("it", "qwen@1")], forceAll: false));

        // "Forza tutto": every captioner available.
        Assert.Equal((true, true), both.CaptionsToRedo([], forceAll: true));
        Assert.Equal((true, false), noVlm.CaptionsToRedo([], forceAll: true));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>The "new models": detector 2, SFace, RAM++ 2, Florence 2; everything a little different.</summary>
    private sealed class NewModels(IFingerprintProtector keys) : IPhotoProcessor
    {
        private int _calls;

        public int Calls => _calls;

        public Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var face = new Face(0, 0, new BoundingBox(0.41f, 0.21f, 0.51f, 0.36f), null, 0.8, EyeState.Unknown, EyeState.Unknown, keys.Protect([0f, 1f, 0f]),
                null, null, null, "culling-v1+det@2", [new Point2(0.41f, 0.21f)], "sface@1/sface");
            return Task.FromResult(new PhotoProcessingResult(
                new AnalyzerResult("culling-v1", "culling-v1+det@2", new Dictionary<string, double> { ["sharpness"] = 0.3 }),
                [face],
                new DecisionProposal(CullingOutcome.ToReview, ["blur"]),
                "engine+culling-v1+det@2",
                new ImageEmbedding("clip@1", [1f, 0f]),
                options.Tagger ? new[] { new TagResult("lake", TagSource.RamPlus, 0.9, "ram@2"), new TagResult("sunset", TagSource.RamPlus, 0.7, "ram@2"), new TagResult("boat", TagSource.RamPlus, 0.4, "ram@2") }
                    .Where(t => t.TagKey != "boat" || photo.Path.EndsWith("p0.jpg", StringComparison.Ordinal)).ToList() : null,
                null,
                options.Captions ? new GeneratedCaption("en", "A lake at sunset.", "florence@2") : null));
        }
    }
}
