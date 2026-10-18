// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Parity;

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class QueryMatcherTests
{
    [Fact]
    public void Swapped_queries_are_paired_and_show_no_difference()
    {
        float[] scores = [0.9f, 0.1f, 0.2f, 0.8f, 0.5f, 0.5f];
        float[] boxes = [0.1f, 0.1f, 0.2f, 0.2f, 0.5f, 0.5f, 0.3f, 0.3f, 0.7f, 0.7f, 0.1f, 0.1f];

        // Reference: queries 0 and 1 in reverse order.
        float[] refScores = [0.2f, 0.8f, 0.9f, 0.1f, 0.5f, 0.5f];
        float[] refBoxes = [0.5f, 0.5f, 0.3f, 0.3f, 0.1f, 0.1f, 0.2f, 0.2f, 0.7f, 0.7f, 0.1f, 0.1f];

        var result = QueryMatcher.Compare(scores, boxes, refScores, refBoxes, classes: 2);

        Assert.Equal([1, 0, 2], result.Assignment);
        Assert.Equal(2, result.MovedQueries);
        Assert.Equal(0, result.MaxScoreDiff);
        Assert.Equal(0, result.MaxBoxDiff);
    }

    [Fact]
    public void Real_differences_remain_visible_after_matching()
    {
        float[] scores = [0.9f, 0.1f];
        float[] boxes = [0.1f, 0.1f, 0.2f, 0.2f];
        float[] refScores = [0.88f, 0.1f];
        float[] refBoxes = [0.1f, 0.1f, 0.2f, 0.25f];

        var result = QueryMatcher.Compare(scores, boxes, refScores, refBoxes, classes: 2);

        Assert.Equal(0, result.MovedQueries);
        Assert.Equal(0.02, result.MaxScoreDiff, 5);
        Assert.Equal(0.05, result.MaxBoxDiff, 5);
    }

    [Fact]
    public void Hungarian_finds_the_optimal_assignment()
    {
        var cost = new double[,] { { 4, 1, 3 }, { 2, 0, 5 }, { 3, 2, 2 } };
        Assert.Equal([1, 0, 2], QueryMatcher.Solve(cost)); // total 1 + 2 + 2 = 5
    }
}
