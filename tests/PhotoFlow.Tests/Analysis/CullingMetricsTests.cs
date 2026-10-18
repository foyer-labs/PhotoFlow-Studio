// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Quality;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>Culling metrics with uncertainty (phase 2 acceptance criterion) and the D23 rule.</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class CullingMetricsTests
{
    [Theory]
    [InlineData(0, 10, 0.0, 0.2775)]
    [InlineData(5, 10, 0.2366, 0.7634)]
    [InlineData(78, 100, 0.6893, 0.8500)]
    [InlineData(1, 100, 0.0018, 0.0545)]
    public void Wilson_intervals_match_the_reference_values(int successes, int total, double low, double high)
    {
        var (l, h) = Proportion.Wilson(successes, total);
        Assert.Equal(low, l, 4);
        Assert.Equal(high, h, 4);
    }

    [Fact]
    public void Metrics_count_the_right_photos_and_the_normal_mode_waits_for_all_three()
    {
        static IEnumerable<LabeledVerdict> Many(int n, PhotoLabel label, CullingOutcome outcome, string profile = "events") =>
            Enumerable.Repeat(new LabeledVerdict(label, outcome, profile), n);

        // 500 blurred photos: 450 discarded, 50 to review; 600 good ones: 1 wrongly discarded, 20 to review.
        var labels = Many(450, PhotoLabel.Blurred, CullingOutcome.Discard)
            .Concat(Many(50, PhotoLabel.Blurred, CullingOutcome.ToReview))
            .Concat(Many(1, PhotoLabel.Good, CullingOutcome.Discard, "portrait"))
            .Concat(Many(20, PhotoLabel.Good, CullingOutcome.ToReview))
            .Concat(Many(579, PhotoLabel.Good, CullingOutcome.Kept))
            .Append(new LabeledVerdict(PhotoLabel.BurstBest, CullingOutcome.Best, "events"))
            .ToList();

        var metrics = CullingMetrics.Compute(labels);

        Assert.Equal(1100, metrics.Labeled);
        Assert.Equal((1, 451), (metrics.WrongDiscards.Proportion.Successes, metrics.WrongDiscards.Proportion.Total));
        Assert.Equal((450, 500), (metrics.Intercepted.Proportion.Successes, metrics.Intercepted.Proportion.Total));
        Assert.Equal((70, 1100), (metrics.ToReview.Proportion.Successes, metrics.ToReview.Proportion.Total));
        Assert.All(new[] { metrics.WrongDiscards, metrics.Intercepted, metrics.ToReview }, m => Assert.Equal(MetricVerdict.WithinTarget, m.Verdict));
        Assert.True(metrics.NormalModeAllowed);
        Assert.Equal(1, metrics.PerProfile["portrait"]);

        // Few labels: the intervals are wide and the prudent mode stays.
        var few = CullingMetrics.Compute(labels.Take(40).Concat(labels.Skip(500).Take(20)).ToList());
        Assert.False(few.NormalModeAllowed);
        Assert.Equal(MetricVerdict.NoData, CullingMetrics.Compute([]).ToReview.Verdict);
    }
}
