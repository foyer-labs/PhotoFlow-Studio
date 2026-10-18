// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Culling;
using PhotoFlow.Analysis.Profiles;
using PhotoFlow.Analysis.Quality;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>
/// Custom profiles (spec §6, board 5e): duplicated from a built-in one, saved as versioned JSON files (the previous
/// versions stay), built-in profiles read-only; and the blur threshold suggested by the corrections.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class ProfileLibraryTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-profiles");

    [Fact]
    public void Custom_profiles_are_versioned_files_and_built_ins_stay_read_only()
    {
        var folder = Path.Combine(_temp.Path, "profiles");
        var library = new ProfileLibrary(folder);
        Assert.Equal(BuiltInProfiles.All.Count, library.All.Count);

        var copy = library.Duplicate("events", "Eventi — chiesa buia");
        Assert.Equal("custom-eventi-chiesa-buia", copy.Id);
        Assert.Equal(1, copy.Version);
        Assert.Equal("events", copy.BasedOn);
        Assert.False(copy.IsBuiltIn);
        Assert.True(File.Exists(Path.Combine(folder, "custom-eventi-chiesa-buia.v1.json")));
        Assert.Equal("custom-eventi-chiesa-buia-2", library.Duplicate("events", "Eventi chiesa buia").Id);

        var saved = library.Save(copy with { Thresholds = copy.Thresholds with { BlurDiscard = 0.30 }, Grouping = new GroupingParameters(1.5, 0.92, 0.03) });
        Assert.Equal(2, saved.Version);
        Assert.True(File.Exists(Path.Combine(folder, "custom-eventi-chiesa-buia.v1.json")));
        Assert.True(File.Exists(Path.Combine(folder, "custom-eventi-chiesa-buia.v2.json")));

        // A new library (next start) reads the latest version; the pipeline gets the new values.
        var reloaded = new ProfileLibrary(folder);
        var latest = reloaded.Get("custom-eventi-chiesa-buia");
        Assert.Equal(2, latest.Version);
        Assert.Equal(0.30, latest.Thresholds.BlurDiscard);
        Assert.Equal(new GroupingParameters(1.5, 0.92, 0.03), latest.Grouping);

        // Built-in profiles cannot be saved; invalid values are refused; unknown ids fall back to Events.
        Assert.Throws<InvalidOperationException>(() => library.Save(BuiltInProfiles.Get("events")));
        Assert.Throws<InvalidDataException>(() => library.Save(latest with { Weights = new ProfileWeights(50, 50, 50, 0, 0) }));
        Assert.Equal("events", reloaded.Get("custom-missing").Id);

        // An unreadable file is reported, never deleted.
        File.WriteAllText(Path.Combine(folder, "custom-broken.v1.json"), "{ not json");
        var withBroken = new ProfileLibrary(folder);
        Assert.Single(withBroken.LoadErrors);
        Assert.True(File.Exists(Path.Combine(folder, "custom-broken.v1.json")));
    }

    [Fact]
    public void Corrections_suggest_a_blur_threshold_and_nothing_changes_by_itself()
    {
        var profile = BuiltInProfiles.Get("events") with { Id = "custom-x", IsBuiltIn = false };
        var corrections = new List<Correction>();

        // 30 photos discarded for blur (sharpness 0.31-0.34) that the user kept; 5 blurred ones (0.20) the user discarded too late.
        for (var i = 0; i < 30; i++)
        {
            corrections.Add(Correction(CullingOutcome.Discard, CullingOutcome.Kept, 0.31 + (i % 4 * 0.01)));
        }

        for (var i = 0; i < 5; i++)
        {
            corrections.Add(Correction(CullingOutcome.ToReview, CullingOutcome.Discard, 0.20));
        }

        var suggestion = ThresholdAdvisor.SuggestBlurDiscard(profile, corrections);
        Assert.NotNull(suggestion);
        Assert.Equal(0.35, suggestion.Current);
        Assert.InRange(suggestion.Value, 0.21, 0.31);
        Assert.Equal(30, suggestion.AvoidedWrong);
        Assert.Equal(0, suggestion.LostCaught);
        Assert.Equal(0.35, profile.Thresholds.BlurDiscard);

        // Too few corrections, or corrections of another profile: no suggestion.
        Assert.Null(ThresholdAdvisor.SuggestBlurDiscard(profile, corrections.Take(10).ToList()));
        Assert.Null(ThresholdAdvisor.SuggestBlurDiscard(BuiltInProfiles.Get("portrait"), corrections));

        static Correction Correction(CullingOutcome automatic, CullingOutcome user, double sharpness) =>
            new(0, 1, automatic, user, "custom-x", CullingScores.ToScores(new CullingSignals(SharpnessZoneKind.AfArea, sharpness, sharpness, 100, 0, 0, 0, 0, 0.5, 0, null, 0, 0, null,
                EyeState.Unknown, EyeState.Unknown, null, null, 0, 0)), DateTimeOffset.UtcNow);
    }

    public void Dispose() => _temp.Dispose();
}
