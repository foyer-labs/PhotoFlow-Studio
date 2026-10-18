// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Tests.Parity;

/// <summary>
/// Compares two sets of detector queries independently of their order (spec §14, decision T1): near-tied
/// queries may swap positions between runtimes, so queries are first paired by an optimal assignment
/// (Hungarian algorithm) on the L1 distance of boxes and class scores, then compared pair by pair.
/// </summary>
public static class QueryMatcher
{
    public sealed record Result(double MaxScoreDiff, double MaxBoxDiff, int MovedQueries, int[] Assignment);

    /// <param name="scores">Actual class scores, [queries × classes].</param>
    /// <param name="boxes">Actual boxes, [queries × 4].</param>
    /// <param name="expectedScores">Reference class scores, [queries × classes].</param>
    /// <param name="expectedBoxes">Reference boxes, [queries × 4].</param>
    public static Result Compare(float[] scores, float[] boxes, float[] expectedScores, float[] expectedBoxes, int classes)
    {
        var queries = boxes.Length / 4;
        if (expectedBoxes.Length != boxes.Length || scores.Length != queries * classes || expectedScores.Length != scores.Length)
        {
            throw new ArgumentException("Actual and reference outputs have different shapes.");
        }

        var cost = new double[queries, queries];
        for (var i = 0; i < queries; i++)
        {
            for (var j = 0; j < queries; j++)
            {
                double c = 0;
                for (var k = 0; k < 4; k++)
                {
                    c += Math.Abs(boxes[i * 4 + k] - expectedBoxes[j * 4 + k]);
                }

                for (var k = 0; k < classes; k++)
                {
                    c += Math.Abs(scores[i * classes + k] - expectedScores[j * classes + k]);
                }

                cost[i, j] = c;
            }
        }

        var assignment = Solve(cost);
        double maxScore = 0, maxBox = 0;
        var moved = 0;
        for (var i = 0; i < queries; i++)
        {
            var j = assignment[i];
            moved += j != i ? 1 : 0;
            for (var k = 0; k < 4; k++)
            {
                maxBox = Math.Max(maxBox, Math.Abs(boxes[i * 4 + k] - expectedBoxes[j * 4 + k]));
            }

            for (var k = 0; k < classes; k++)
            {
                maxScore = Math.Max(maxScore, Math.Abs(scores[i * classes + k] - expectedScores[j * classes + k]));
            }
        }

        return new Result(maxScore, maxBox, moved, assignment);
    }

    /// <summary>Hungarian algorithm (potentials, O(n³)) for a square cost matrix; returns row → column.</summary>
    public static int[] Solve(double[,] cost)
    {
        var n = cost.GetLength(0);
        var u = new double[n + 1];
        var v = new double[n + 1];
        var p = new int[n + 1];
        var way = new int[n + 1];
        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = new double[n + 1];
            var used = new bool[n + 1];
            Array.Fill(minv, double.PositiveInfinity);
            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = 0;
                var delta = double.PositiveInfinity;
                for (var j = 1; j <= n; j++)
                {
                    if (used[j])
                    {
                        continue;
                    }

                    var current = cost[i0 - 1, j - 1] - u[i0] - v[j];
                    if (current < minv[j])
                    {
                        minv[j] = current;
                        way[j] = j0;
                    }

                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }

                for (var j = 0; j <= n; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }

                j0 = j1;
            }
            while (p[j0] != 0);

            do
            {
                var j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            }
            while (j0 != 0);
        }

        var assignment = new int[n];
        for (var j = 1; j <= n; j++)
        {
            assignment[p[j] - 1] = j - 1;
        }

        return assignment;
    }
}
