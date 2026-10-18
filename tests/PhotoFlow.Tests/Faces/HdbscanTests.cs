// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Faces.Clustering;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Faces;

/// <summary>HDBSCAN on synthetic fingerprints (the comparison with scikit-learn is in the parity tests).</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class HdbscanTests
{
    [Theory]
    [InlineData(1000)]
    [InlineData(15)]
    public void Separate_people_become_separate_clusters(int neighbors)
    {
        var random = new Random(3);
        var points = new List<float[]>();
        var truth = new List<int>();
        for (var person = 0; person < 3; person++)
        {
            var center = Enumerable.Range(0, 32).Select(_ => (float)random.NextDouble() - 0.5f).ToArray();
            for (var i = 0; i < 10 + (person * 5); i++)
            {
                points.Add(Vectors.Normalize(center.Select(v => v + ((float)random.NextDouble() - 0.5f) * 0.03f).ToArray()));
                truth.Add(person);
            }
        }

        for (var stranger = 0; stranger < 2; stranger++)
        {
            points.Add(Vectors.Normalize(Enumerable.Range(0, 32).Select(_ => (float)random.NextDouble() - 0.5f).ToArray()));
            truth.Add(-1);
        }

        var labels = Hdbscan.Cluster(points, minClusterSize: 3, minSamples: 2, neighbors, ClusterSelection.ExcessOfMass);

        Assert.Equal(3, labels.Max() + 1);
        for (var person = 0; person < 3; person++)
        {
            var assigned = labels.Where((_, i) => truth[i] == person).Distinct().ToList();
            Assert.Single(assigned);
            Assert.True(assigned[0] >= 0);
        }

        Assert.Contains(-1, labels.Where((_, i) => truth[i] < 0)); // a random face may sit near someone; not all of them
    }

    [Fact]
    public void Too_few_points_are_all_noise()
    {
        Assert.Equal([-1, -1], Hdbscan.Cluster([[1f, 0f], [0f, 1f]], 3, 2, 10, ClusterSelection.ExcessOfMass));
    }
}
