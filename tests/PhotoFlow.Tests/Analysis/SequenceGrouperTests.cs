// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Grouping;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>Bursts and special sequences (spec §6): bracketing and timelapse are never bursts (acceptance criterion of phase 2).</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class SequenceGrouperTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 15, 0, 0, TimeSpan.FromHours(2));
    private static readonly GroupingParameters Parameters = GroupingParameters.Default;

    [Fact]
    public void Close_similar_shots_form_a_burst_and_a_change_of_subject_splits_it()
    {
        var photos = new[]
        {
            Photo(1, 0.0, Scene(1)), Photo(2, 0.2, Scene(1, 0.05)), Photo(3, 0.4, Scene(1, 0.1)),
            Photo(4, 0.6, Scene(2)), Photo(5, 0.8, Scene(2, 0.05)),
            Photo(6, 5.0, Scene(2, 0.1)), // too late: single
        };

        var groups = SequenceGrouper.Group(photos, Parameters);

        Assert.Equal(["Burst:1,2,3", "Burst:4,5"], Simplify(groups));
    }

    [Fact]
    public void Exposure_bracketing_is_never_a_burst()
    {
        var photos = new[]
        {
            Photo(1, 0.0, Scene(1), compensation: 0), Photo(2, 0.15, Scene(1), compensation: -2), Photo(3, 0.3, Scene(1), compensation: 2),
            Photo(4, 6.0, Scene(1), exposure: 10), Photo(5, 6.2, Scene(1), exposure: 11), Photo(6, 6.4, Scene(1), exposure: 12), // manual
        };

        var groups = Simplify(SequenceGrouper.Group(photos, Parameters));

        Assert.Equal(["Bracketing:1,2,3", "Bracketing:4,5,6"], groups);
        Assert.DoesNotContain(groups, g => g.StartsWith("Burst", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(1.0, false)]
    [InlineData(3.0, true)]
    [InlineData(30.0, false)]
    public void A_timelapse_is_never_a_burst_even_when_shots_are_close_and_similar(double interval, bool subSeconds)
    {
        var photos = Enumerable.Range(0, 24)
            .Select(i => Photo(i + 1, (i * interval) + (subSeconds ? 0.37 : 0), Scene(1, 0.001 * i)))
            .ToList();

        var groups = Simplify(SequenceGrouper.Group(photos, Parameters));

        Assert.Equal([$"Timelapse:{string.Join(',', Enumerable.Range(1, 24))}"], groups);
    }

    [Fact]
    public void A_long_fast_burst_stays_a_burst()
    {
        var photos = Enumerable.Range(0, 30).Select(i => Photo(i + 1, i * 0.1, Scene(1, 0.002 * i))).ToList();

        Assert.Equal([$"Burst:{string.Join(',', Enumerable.Range(1, 30))}"], Simplify(SequenceGrouper.Group(photos, Parameters)));
    }

    [Fact]
    public void Two_cameras_shooting_together_are_grouped_separately()
    {
        var photos = new[]
        {
            Photo(1, 0.0, Scene(1), camera: "Z5_2"), Photo(2, 0.1, Scene(1), camera: "D750"),
            Photo(3, 0.3, Scene(1), camera: "Z5_2"), Photo(4, 0.4, Scene(1), camera: "D750"),
        };

        Assert.Equal(["Burst:1,3", "Burst:2,4"], Simplify(SequenceGrouper.Group(photos, Parameters)).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Without_capture_time_only_similarity_groups_and_without_embeddings_nothing()
    {
        var photos = new[]
        {
            Photo(1, null, Scene(1)), Photo(2, null, Scene(1, 0.05)), Photo(3, null, Scene(3)), Photo(4, null, null), Photo(5, null, null),
        };

        Assert.Equal(["Burst:1,2"], Simplify(SequenceGrouper.Group(photos, Parameters)));
    }

    [Fact]
    public void Without_embeddings_time_alone_makes_the_burst()
    {
        var photos = new[] { Photo(1, 0.0, null), Photo(2, 0.5, null), Photo(3, 1.0, null), Photo(4, 9.0, null) };

        Assert.Equal(["Burst:1,2,3"], Simplify(SequenceGrouper.Group(photos, Parameters)));
    }

    private static List<string> Simplify(IReadOnlyList<(GroupType Type, IReadOnlyList<long> AssetIds)> groups) =>
        groups.Select(g => $"{g.Type}:{string.Join(',', g.AssetIds)}").ToList();

    private static GroupingPhoto Photo(long id, double? seconds, float[]? embedding, double? compensation = null, double? exposure = null, string camera = "Z5_2") =>
        new(id, (int)id, seconds is { } s ? T0.AddSeconds(s) : null, camera, compensation, exposure, embedding);

    /// <summary>A unit vector for a scene, slightly rotated by <paramref name="drift"/> (cosine ≈ 1 − drift²/2).</summary>
    private static float[] Scene(int scene, double drift = 0)
    {
        var vector = new float[8];
        vector[scene] = (float)Math.Cos(drift);
        vector[0] = (float)Math.Sin(drift);
        return Vectors.Normalize(vector);
    }
}
