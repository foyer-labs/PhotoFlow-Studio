// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using PhotoFlow.Analysis.Culling;
using PhotoFlow.Analysis.Grouping;
using PhotoFlow.Analysis.Profiles;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Data;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tagging.Clip;
using PhotoFlow.Tests.Sources;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>Scene profile per group (D8): zero-shot classifier, automatic assignment with fallback, suggestion, re-analysis.</summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class ProfileStageTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-profiles");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait(TestCategory.Name, TestCategory.Unit)]
    public async Task The_classifier_ranks_the_profile_whose_prompts_match_the_image()
    {
        var classifier = new ClipSceneClassifier(new KeywordTextEmbedder());

        var ranking = await classifier.ClassifyAsync(KeywordTextEmbedder.Axis("racing"), Ct);

        Assert.Equal("motorsport", ranking[0].ProfileId);
        Assert.True(ranking[0].Probability > 0.9);
        Assert.Equal(1.0, ranking.Sum(r => r.Probability), 6);
        Assert.DoesNotContain(ranking, r => r.ProfileId == "archive");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Groups_get_their_profile_automatically_or_as_a_suggestion_and_are_analyzed_again(bool automatic)
    {
        var root = _temp.Sub("archive");
        for (var i = 1; i <= 4; i++)
        {
            SyntheticImages.WriteJpeg(Path.Combine(root, $"P_{i}.JPG"), 64, 48, i);
        }

        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "A", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);
        var fs = new PhysicalFileSystem();
        var jobId = await ArchiveJobRunner.CreateAsync(catalog, fs, new ArchiveJobSettings(libraryId, "events", ArchiveOrder.ByFolder, FullHash: false), Ct);
        if (automatic)
        {
            await catalog.Jobs.SetAutomaticProfileAsync(jobId, true, "events", Ct);
        }

        var processor = new ProfileRecorder();
        var classifier = new FixedClassifier(new Dictionary<string, (string, double)>
        {
            ["P_1"] = ("motorsport", 0.85), ["P_2"] = ("motorsport", 0.85), ["P_3"] = ("landscape", 0.40), ["P_4"] = ("events", 0.95),
        });
        IReadOnlyList<IBatchStage> stages =
        [
            new ProfileStage(catalog, classifier, ProfileRecorder.EmbeddingVersion),
            new ReanalysisStage(catalog, fs, processor, null),
            new GroupDecisionStage(catalog, BuiltInProfiles.Get, new DecisionEngine()),
        ];
        await new ArchiveJobRunner(catalog, new FakeExifReader(_ => null), fs, processor, stages).RunAsync(jobId, null, Ct);

        var assetOf = (await catalog.Jobs.GetItemsAsync(jobId, Ct)).ToDictionary(i => Path.GetFileNameWithoutExtension(i.SourcePath), i => i.AssetId!.Value);
        var profiles = await catalog.Groups.GetPhotoProfilesAsync(jobId, Ct);
        if (automatic)
        {
            Assert.Equal("motorsport", profiles[assetOf["P_1"]].ProfileId);
            Assert.Equal("events", profiles[assetOf["P_3"]].ProfileId); // under the threshold: fallback
            Assert.Equal(["P_1:motorsport", "P_2:motorsport"], processor.Reanalyzed.Order(StringComparer.Ordinal));
            Assert.Equal("motorsport", (await catalog.Analysis.GetResultAsync(assetOf["P_1"], CullingAnalyzer.AnalyzerId, Ct))!.ProfileId);
            Assert.Contains("motorsport", (await catalog.Decisions.GetAsync(assetOf["P_1"], Ct))!.ModelVersion, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(profiles[assetOf["P_1"]].ProfileId);
            Assert.Equal("motorsport", profiles[assetOf["P_1"]].SuggestedProfileId);
            Assert.Null(profiles[assetOf["P_3"]].SuggestedProfileId); // not confident enough
            Assert.Null(profiles[assetOf["P_4"]].SuggestedProfileId); // same as the job
            Assert.Empty(processor.Reanalyzed);
        }

        // The user's choice for a photo is never replaced by the automatic assignment.
        await catalog.Groups.SetUserProfileAsync(jobId, [assetOf["P_4"]], "portrait", Ct);
        foreach (var stage in stages)
        {
            await stage.RunAsync(new JobContext(jobId, JobType.AnalyzeInPlace, "events"), null, Ct);
        }

        var after = await catalog.Groups.GetPhotoProfilesAsync(jobId, Ct);
        Assert.Equal("portrait", after[assetOf["P_4"]].ProfileId);
        Assert.True(after[assetOf["P_4"]].ByUser);
        Assert.Contains("P_4:portrait", processor.Reanalyzed);
    }

    [Fact]
    public async Task A_profile_that_measures_the_same_way_changes_the_label_without_a_new_analysis()
    {
        var root = _temp.Sub("same");
        SyntheticImages.WriteJpeg(Path.Combine(root, "P_1.JPG"), 64, 48, 1);
        SyntheticImages.WriteJpeg(Path.Combine(root, "P_2.JPG"), 64, 48, 2);
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "s.pfdb"), Path.Combine(_temp.Path, "sb")), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "A", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);
        var fs = new PhysicalFileSystem();
        var jobId = await ArchiveJobRunner.CreateAsync(catalog, fs, new ArchiveJobSettings(libraryId, "events", ArchiveOrder.ByFolder, FullHash: false), Ct);
        await catalog.Jobs.SetAutomaticProfileAsync(jobId, true, "events", Ct);

        // "custom-quiet" differs from Events only in weights and decision thresholds: same zones, subjects, faces, eyes.
        var quiet = BuiltInProfiles.Get("events") with { Id = "custom-quiet", IsBuiltIn = false, Weights = new ProfileWeights(50, 20, 10, 10, 10) };
        SceneProfile Resolve(string id) => id == quiet.Id ? quiet : BuiltInProfiles.Get(id);
        var processor = new ProfileRecorder();
        var classifier = new FixedClassifier(new Dictionary<string, (string, double)> { ["P_1"] = ("custom-quiet", 0.9), ["P_2"] = ("motorsport", 0.9) });
        IReadOnlyList<IBatchStage> stages =
        [
            new ProfileStage(catalog, classifier, ProfileRecorder.EmbeddingVersion),
            new ReanalysisStage(catalog, fs, processor, null, Resolve, parallelism: 4),
        ];
        await new ArchiveJobRunner(catalog, new FakeExifReader(_ => null), fs, processor, stages).RunAsync(jobId, null, Ct);

        var assetOf = (await catalog.Jobs.GetItemsAsync(jobId, Ct)).ToDictionary(i => Path.GetFileNameWithoutExtension(i.SourcePath), i => i.AssetId!.Value);
        Assert.Equal(["P_2:motorsport"], processor.Reanalyzed);
        Assert.Equal("custom-quiet", (await catalog.Analysis.GetResultAsync(assetOf["P_1"], CullingAnalyzer.AnalyzerId, Ct))!.ProfileId);
        Assert.Equal("motorsport", (await catalog.Analysis.GetResultAsync(assetOf["P_2"], CullingAnalyzer.AnalyzerId, Ct))!.ProfileId);
        Assert.NotEqual(CullingScores.MeasurementKey(BuiltInProfiles.Get("events")), CullingScores.MeasurementKey(BuiltInProfiles.Get("motorsport")));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Text vectors on axes chosen by keywords of the prompts.</summary>
    private sealed class KeywordTextEmbedder : ITextEmbedder
    {
        private static readonly string[] Keywords = ["wedding", "portrait", "racing", "animal", "landscape", "night"];

        public string ModelVersion => "keywords@1";

        public static float[] Axis(string keyword)
        {
            var vector = new float[Keywords.Length + 1];
            vector[Array.IndexOf(Keywords, keyword) + 1] = 1;
            return vector;
        }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct)
        {
            var vector = new float[Keywords.Length + 1];
            vector[0] = 0.2f;
            for (var i = 0; i < Keywords.Length; i++)
            {
                if (text.Contains(Keywords[i], StringComparison.Ordinal) || (i == 2 && text.Contains("motor", StringComparison.Ordinal)))
                {
                    vector[i + 1] = 1;
                }
            }

            return Task.FromResult(Vectors.Normalize(vector));
        }
    }

    /// <summary>Classification decided per file through the embedding (one axis per file).</summary>
    private sealed class FixedClassifier(IReadOnlyDictionary<string, (string Profile, double Probability)> byName) : ISceneClassifier
    {
        public string ModelVersion => "fixed@1";

        public Task<IReadOnlyList<(string ProfileId, double Probability)>> ClassifyAsync(ReadOnlyMemory<float> imageEmbedding, CancellationToken ct)
        {
            var index = imageEmbedding.Span.IndexOf(imageEmbedding.Span.ToArray().Max());
            var (profile, probability) = byName[$"P_{index}"];
            return Task.FromResult<IReadOnlyList<(string, double)>>([(profile, probability), ("other", 1 - probability)]);
        }
    }

    /// <summary>Records the profile of every analysis after the first pass; embeds each file on its own axis.</summary>
    private sealed class ProfileRecorder : IPhotoProcessor
    {
        public const string EmbeddingVersion = "test-clip@1";
        private readonly ConcurrentDictionary<string, int> _seen = new();

        public ConcurrentBag<string> Reanalyzed { get; } = [];

        public Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct)
        {
            var name = Path.GetFileNameWithoutExtension(photo.Path);
            if (_seen.AddOrUpdate(name, 1, (_, n) => n + 1) > 1)
            {
                Reanalyzed.Add($"{name}:{options.ProfileId}");
            }

            var signals = new CullingSignals(SharpnessZoneKind.Tiles, 0.8, 0.8, 200, 0, 0, 0, 0, 0.5, 0, null, 0, 0, null, EyeState.Unknown, EyeState.Unknown, null, null, 0, 0);
            var vector = new float[8];
            vector[int.Parse(name[2..], System.Globalization.CultureInfo.InvariantCulture)] = 1;
            return Task.FromResult(new PhotoProcessingResult(
                new AnalyzerResult(CullingAnalyzer.AnalyzerId, "test@1", CullingScores.ToScores(signals)),
                [],
                new DecisionProposal(CullingOutcome.Kept, []),
                "decision-test",
                new ImageEmbedding(EmbeddingVersion, vector)));
        }
    }
}
