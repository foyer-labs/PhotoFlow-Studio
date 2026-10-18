// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using PhotoFlow.Analysis.Culling;
using PhotoFlow.Analysis.Grouping;
using PhotoFlow.Analysis.Profiles;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Data;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>
/// Groupings and decisions per group through a real job (phase 2, stages 4-5): synthetic JPEGs whose EXIF (sub-second
/// capture times, exposure compensation, camera) is written by exiftool and read back by the job; the signals and the
/// embeddings come from a stand-in analysis. Bracketing and timelapse are never bursts; the user's decisions stay.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class GroupingStageTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 12, 15, 0, 0);
    private readonly TempFolder _temp = new("pf-groups");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bursts_are_ranked_special_sequences_are_not_and_user_decisions_stay()
    {
        var root = _temp.Sub("archive");
        await using var exifTool = ExifToolFixture.Start();
        var shots = new List<(string Name, double Seconds, int Scene, double Detail, double? Compensation, bool Blurred)>
        {
            ("A_01", 0.0, 1, 100, null, false), ("A_02", 0.2, 1, 300, null, false), ("A_03", 0.4, 1, 200, null, false),
            ("A_04", 0.6, 1, 150, null, false), ("A_05", 0.8, 1, 120, null, false),
            ("B_01", 10.0, 2, 200, null, false), ("B_02", 10.2, 2, 200, null, false),
            ("C_01", 20.0, 3, 200, 0, false), ("C_02", 20.15, 3, 200, -2, false), ("C_03", 20.3, 3, 200, 2, false),
            ("E_01", 100.0, 5, 200, null, false), ("E_02", 100.3, 5, 220, null, false), ("E_03", 100.6, 6, 200, null, false),
            ("F_01", 200.0, 7, 300, null, true), ("F_02", 200.4, 7, 200, null, false),
        };
        shots.AddRange(Enumerable.Range(1, 12).Select(i => ($"D_{i:00}", 30 + (i * 5.0), 4, 200.0, (double?)null, false)));
        foreach (var shot in shots)
        {
            var path = SyntheticImages.WriteJpeg(Path.Combine(root, shot.Name + ".JPG"), 64, 48, shot.Name.GetHashCode(StringComparison.Ordinal));
            var time = T0.AddSeconds(shot.Seconds);
            var arguments = new List<string>
            {
                "-overwrite_original", "-Model=Z 5_2",
                "-DateTimeOriginal=" + time.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture),
                "-SubSecTimeOriginal=" + time.ToString("fff", CultureInfo.InvariantCulture),
                "-OffsetTimeOriginal=+02:00",
            };
            if (shot.Compensation is { } ev)
            {
                arguments.Add("-ExposureCompensation=" + ev.ToString(CultureInfo.InvariantCulture));
            }

            arguments.Add(path);
            await exifTool.ExecuteAsync(arguments, Ct);
        }

        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "Archivio", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);
        var fs = new PhysicalFileSystem();
        var jobId = await ArchiveJobRunner.CreateAsync(catalog, fs, new ArchiveJobSettings(libraryId, "events", ArchiveOrder.ByFolder, FullHash: false), Ct);
        var processor = new SignalsProcessor(shots.ToDictionary(s => s.Name, s => (s.Scene, s.Detail, s.Blurred)));
        IReadOnlyList<IBatchStage> stages =
        [
            new GroupingStage(catalog, BuiltInProfiles.Get, SignalsProcessor.EmbeddingVersion),
            new GroupDecisionStage(catalog, BuiltInProfiles.Get, new DecisionEngine()),
        ];
        var runner = new ArchiveJobRunner(catalog, new ExifToolExifReader(exifTool), fs, processor, stages);
        Assert.Equal(JobState.Completed, (await runner.RunAsync(jobId, null, Ct)).State);

        // The user rejects one photo of the burst; then the stages run again (resume): the decision stays (I5).
        var assetOf = (await catalog.Jobs.GetItemsAsync(jobId, Ct)).ToDictionary(i => Path.GetFileNameWithoutExtension(i.SourcePath), i => i.AssetId!.Value);
        await catalog.Decisions.SetUserAsync(assetOf["A_03"], CullingOutcome.Discard, null, null, Ct);
        foreach (var stage in stages)
        {
            await stage.RunAsync(new JobContext(jobId, JobType.AnalyzeInPlace, "events"), null, Ct);
        }

        var groups = (await catalog.Groups.ListAsync(jobId, Ct))
            .Select(g => $"{g.Group.Type}:{string.Join(',', g.Members.Select(m => assetOf.Single(a => a.Value == m.AssetId).Key))}")
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
        [
            "Bracketing:C_01,C_02,C_03",
            "Burst:A_01,A_02,A_03,A_04,A_05",
            "Burst:B_01,B_02",
            "Burst:E_01,E_02",
            "Burst:F_01,F_02",
            $"Timelapse:{string.Join(',', Enumerable.Range(1, 12).Select(i => $"D_{i:00}"))}",
        ], groups);

        async Task<CullingOutcome?> Automatic(string name) => (await catalog.Decisions.GetAsync(assetOf[name], Ct))!.AutomaticOutcome;
        Assert.Equal(CullingOutcome.Best, await Automatic("A_02"));
        Assert.Equal(CullingOutcome.Similar, await Automatic("A_01"));
        Assert.Equal(CullingOutcome.ToReview, await Automatic("B_01"));
        Assert.Equal(CullingOutcome.ToReview, await Automatic("B_02"));
        Assert.Equal(CullingOutcome.Discard, await Automatic("F_01"));
        Assert.Equal(CullingOutcome.Best, await Automatic("F_02"));
        Assert.Equal(CullingOutcome.Kept, await Automatic("E_03"));
        foreach (var special in new[] { "C_01", "C_02", "C_03", "D_01", "D_12" })
        {
            Assert.Equal(CullingOutcome.Kept, await Automatic(special)); // never "best" or "similar"
        }

        var userDecision = (await catalog.Decisions.GetAsync(assetOf["A_03"], Ct))!;
        Assert.Equal(CullingOutcome.Discard, userDecision.UserOutcome);
        Assert.Equal(CullingOutcome.Discard, userDecision.EffectiveOutcome);
    }

    [Fact]
    public async Task Manual_groups_survive_the_automatic_grouping_and_take_their_photos()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "m.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var jobId = await catalog.Jobs.CreateAsync(new Job(0, JobType.AnalyzeInPlace, null, "x", null, "events", false, null, JobState.Pending, JobPriority.Low, "{}", DateTimeOffset.UtcNow), Ct);
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await catalog.Assets.AddAsync(new Asset(0, $"q{i}", null, "JPG", "jpeg", null, null, null, DateSource.Unknown, null, AssetState.Active), Ct));
        }

        await catalog.Groups.ReplaceAutomaticAsync(jobId, [(GroupType.Burst, [ids[0], ids[1], ids[2]])], Ct);
        await catalog.Groups.AddManualAsync(jobId, GroupType.Panorama, [ids[2], ids[3], ids[4]], Ct);
        var groups = await catalog.Groups.ReplaceAutomaticAsync(jobId, [(GroupType.Burst, [ids[0], ids[1], ids[2]]), (GroupType.Burst, [ids[3], ids[4]])], Ct);

        Assert.Equal(2, groups.Count);
        var panorama = Assert.Single(groups, g => g.Group.Origin == GroupOrigin.Manual);
        Assert.Equal([ids[2], ids[3], ids[4]], panorama.Members.Select(m => m.AssetId));
        Assert.False(panorama.Group.IsRanked);
        var burst = Assert.Single(groups, g => g.Group.Origin == GroupOrigin.Automatic);
        Assert.Equal([ids[0], ids[1]], burst.Members.Select(m => m.AssetId));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Stand-in analysis: signals and embeddings per file name (scene vector, Laplacian detail, blur).</summary>
    private sealed class SignalsProcessor(IReadOnlyDictionary<string, (int Scene, double Detail, bool Blurred)> shots) : IPhotoProcessor
    {
        public const string EmbeddingVersion = "test-clip@1";

        public Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct)
        {
            var (scene, detail, blurred) = shots[Path.GetFileNameWithoutExtension(photo.Path)];
            var sharpness = blurred ? 0.2 : 0.8;
            var signals = new CullingSignals(SharpnessZoneKind.Tiles, sharpness, sharpness, detail, 0, 0, 0, 0, 0.5, 0, null, 0, 0, null,
                EyeState.Unknown, EyeState.Unknown, null, null, 0, 0);
            var vector = new float[16];
            vector[scene] = 1;
            var decision = new DecisionEngine().Decide(signals, BuiltInProfiles.Get(options.ProfileId));
            return Task.FromResult(new PhotoProcessingResult(
                new AnalyzerResult(CullingAnalyzer.AnalyzerId, "test@1", CullingScores.ToScores(signals)),
                [],
                decision,
                "decision-test",
                new ImageEmbedding(EmbeddingVersion, vector)));
        }
    }
}
