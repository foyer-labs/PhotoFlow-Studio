// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoFlow.Faces.Clustering;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Parity;

/// <summary>
/// The C# HDBSCAN against scikit-learn (references from tools/lab/hdbscan_refs.py). The single-linkage heights are
/// unique and must match; with tied mutual reachability distances numpy's unstable sort orders equal merges at
/// random, so the partitions must agree on at least <see cref="MinRandIndex"/> of the point pairs (identical when
/// there are no ties; the production setting min_samples = 2 is identical). The leaf selection and a 15-neighbor graph
/// are reported.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Parity)]
public sealed class HdbscanParityTests
{
    public const double MinRandIndex = 0.97;
    public const double MaxHeightDifference = 1e-9;

    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    [Fact]
    public void Partitions_match_scikit_learn()
    {
        var folder = TestData.RequireDirectory("parity", "hdbscan");
        var indexPath = Path.Combine(folder, "index.json");
        if (!File.Exists(indexPath))
        {
            Assert.Skip($"References missing: run tools/lab/hdbscan_refs.py ({indexPath}).");
        }

        var failures = new List<string>();
        var approximate = new List<object>();
        var exactRuns = new List<object>();
        foreach (var set in JsonNode.Parse(File.ReadAllText(indexPath))!["sets"]!.AsArray().Select(s => s!))
        {
            var (name, n, d) = (set["name"]!.GetValue<string>(), set["n"]!.GetValue<int>(), set["d"]!.GetValue<int>());
            var flat = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(folder, name + ".vectors.bin"))).ToArray();
            var points = Enumerable.Range(0, n).Select(i => flat.AsSpan(i * d, d).ToArray()).ToList();
            foreach (var run in set["runs"]!.AsArray().Select(r => r!))
            {
                var (mcs, ms) = (run["min_cluster_size"]!.GetValue<int>(), run["min_samples"]!.GetValue<int>());
                var heights = run["heights"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
                var actualHeights = Hdbscan.LinkageHeights(points, ms);
                var heightDifference = heights.Zip(actualHeights).Max(h => Math.Abs(h.First - h.Second));
                if (heightDifference > MaxHeightDifference)
                {
                    failures.Add($"{name} ms={ms}: linkage heights differ by {heightDifference:E2}");
                }

                foreach (var (key, selection) in new[] { ("eom", ClusterSelection.ExcessOfMass), ("leaf", ClusterSelection.Leaf) })
                {
                    var expected = run[key]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
                    var exact = Hdbscan.Cluster(points, mcs, ms, n, selection);
                    var agreement = SamePartition(exact, expected) ? 1 : RandIndex(exact, expected);
                    exactRuns.Add(new { name, mcs, ms, selection = key, randIndex = agreement, clusters = exact.Max() + 1, expectedClusters = expected.Max() + 1 });
                    // The leaf selection is reported only: it is very sensitive to the order of tied merges (F2-6).
                    if (selection == ClusterSelection.ExcessOfMass && (agreement < MinRandIndex || (ms <= 2 && agreement < 1)))
                    {
                        failures.Add($"{name} mcs={mcs} ms={ms} {key}: Rand index {agreement:F4} ({exact.Max() + 1} clusters / {exact.Count(l => l < 0)} noise, expected {expected.Max() + 1} / {expected.Count(l => l < 0)})");
                    }

                    var knn = Hdbscan.Cluster(points, mcs, ms, 15, selection);
                    approximate.Add(new { name, mcs, ms, selection = key, randIndex = RandIndex(knn, expected) });
                }
            }
        }

        var report = JsonSerializer.Serialize(new { failures, exactRuns, approximate }, ReportJson);
        File.WriteAllText(Path.Combine(TestData.RequireDirectory("parity", "reports"), $"hdbscan-{DateTime.Now:yyyyMMdd-HHmmss}.json"), report);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Same clusters up to the numbering, same noise points.</summary>
    private static bool SamePartition(int[] a, int[] b)
    {
        var map = new Dictionary<int, int>();
        var reverse = new Dictionary<int, int>();
        for (var i = 0; i < a.Length; i++)
        {
            if ((a[i] < 0) != (b[i] < 0))
            {
                return false;
            }

            if (a[i] < 0)
            {
                continue;
            }

            if ((map.TryGetValue(a[i], out var m) && m != b[i]) || (reverse.TryGetValue(b[i], out var r) && r != a[i]))
            {
                return false;
            }

            map[a[i]] = b[i];
            reverse[b[i]] = a[i];
        }

        return true;
    }

    /// <summary>Fraction of point pairs on which the two labelings agree (noise points each on their own).</summary>
    private static double RandIndex(int[] a, int[] b)
    {
        long agree = 0, total = 0;
        for (var i = 0; i < a.Length; i++)
        {
            for (var j = i + 1; j < a.Length; j++)
            {
                var sameA = a[i] >= 0 && a[i] == a[j];
                var sameB = b[i] >= 0 && b[i] == b[j];
                agree += sameA == sameB ? 1 : 0;
                total++;
            }
        }

        return (double)agree / total;
    }
}
