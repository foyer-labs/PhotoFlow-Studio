// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Faces.Clustering;

public enum ClusterSelection
{
    /// <summary>Excess of mass: the most stable clusters (larger groups).</summary>
    ExcessOfMass,

    /// <summary>Leaves of the cluster tree: finer groups ("soglie che preferiscono dividere", spec §7; F2-6).</summary>
    Leaf,
}

/// <summary>
/// HDBSCAN (Campello et al. 2013; McInnes et al., the <c>hdbscan</c> library and scikit-learn's port) on L2-normalized
/// vectors with the cosine distance, written in C# because HdbscanSharp is O(n²) in memory and inactive (R18, F2-2).
/// The mutual reachability graph is limited to the <c>neighbors</c> nearest points of each point (k-NN graph, exact
/// brute-force search): with <c>neighbors ≥ n − 1</c> the result is exact HDBSCAN. Steps: core distances, mutual
/// reachability minimum spanning tree (Kruskal; components left apart join at infinite distance), single-linkage
/// hierarchy, condensed tree with the minimum cluster size, stability, selection, labels (−1 = noise).
/// </summary>
public static class Hdbscan
{
    public static int[] Cluster(IReadOnlyList<float[]> points, int minClusterSize, int minSamples, int neighbors, ClusterSelection selection)
    {
        ArgumentNullException.ThrowIfNull(points);
        var n = points.Count;
        var labels = Enumerable.Repeat(-1, n).ToArray();
        if (n < Math.Max(2, minClusterSize))
        {
            return labels;
        }

        var k = Math.Min(Math.Max(neighbors, minSamples), n - 1);
        var mst = k >= n - 1 ? ExactTree(points, minSamples) : KnnTree(points, minSamples, k);
        var hierarchy = SingleLinkage(mst, n);
        var condensed = Condense(hierarchy, n, minClusterSize);
        var stability = Stability(condensed);
        var clusters = Select(condensed, stability, selection);
        return Label(condensed, clusters, n);
    }

    /// <summary>
    /// Exact mutual reachability tree, as scikit-learn: dense distances (vectors normalized again in double), core
    /// distance = distance to the <c>min_samples</c>-th point counting the point itself, Prim from point 0 keeping the
    /// first minimum, edges recorded in discovery order (the stable sort that follows keeps ties in that order).
    /// </summary>
    private static List<(int A, int B, double W)> ExactTree(IReadOnlyList<float[]> points, int minSamples)
    {
        var n = points.Count;
        var unit = points.Select(p =>
        {
            var norm = Math.Sqrt(p.Sum(v => v * (double)v));
            return p.Select(v => v / norm).ToArray();
        }).ToArray();
        var distance = new double[n][];
        Parallel.For(0, n, i =>
        {
            var row = new double[n];
            for (var j = 0; j < n; j++)
            {
                var dot = 0.0;
                for (var t = 0; t < unit[i].Length; t++)
                {
                    dot += unit[i][t] * unit[j][t];
                }

                row[j] = i == j ? 0 : Math.Clamp(1 - dot, 0, 2);
            }

            distance[i] = row;
        });

        var core = new double[n];
        for (var i = 0; i < n; i++)
        {
            var sorted = (double[])distance[i].Clone();
            Array.Sort(sorted);
            core[i] = sorted[Math.Min(minSamples - 1, n - 1)];
        }

        var labels = Enumerable.Range(0, n).ToList();
        var minReach = Enumerable.Repeat(double.PositiveInfinity, n).ToList();
        var current = 0;
        var mst = new List<(int A, int B, double W)>(n - 1);
        for (var step = 0; step < n - 1; step++)
        {
            var keep = new List<int>(labels.Count);
            var reach = new List<double>(labels.Count);
            for (var i = 0; i < labels.Count; i++)
            {
                if (labels[i] == current)
                {
                    continue;
                }

                var other = labels[i];
                var mutual = Math.Max(Math.Max(core[current], core[other]), distance[current][other]);
                keep.Add(other);
                reach.Add(Math.Min(minReach[i], mutual));
            }

            labels = keep;
            minReach = reach;
            var best = 0;
            for (var i = 1; i < minReach.Count; i++)
            {
                if (minReach[i] < minReach[best])
                {
                    best = i;
                }
            }

            mst.Add((current, labels[best], minReach[best]));
            current = labels[best];
        }

        return mst;
    }

    /// <summary>Approximate tree on the k-NN graph (Kruskal); components left apart join at infinite distance.</summary>
    private static List<(int A, int B, double W)> KnnTree(IReadOnlyList<float[]> points, int minSamples, int k)
    {
        var n = points.Count;
        var (neighborIds, neighborDistances) = NearestNeighbors(points, k);

        // sklearn: the core distance is the distance to the min_samples-th neighbor counting the point itself.
        var core = new double[n];
        for (var i = 0; i < n; i++)
        {
            core[i] = minSamples <= 1 ? 0 : neighborDistances[i][Math.Min(minSamples - 2, k - 1)];
        }

        var edges = new List<(int A, int B, double W)>(n * k);
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < k; j++)
            {
                var other = neighborIds[i][j];
                if (other > i || !neighborIds[other].Contains(i))
                {
                    edges.Add((i, other, Math.Max(Math.Max(core[i], core[other]), neighborDistances[i][j])));
                }
            }
        }

        edges.Sort((x, y) => x.W != y.W ? x.W.CompareTo(y.W) : x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        var mst = new List<(int A, int B, double W)>(n - 1);
        var forest = new UnionFind(n);
        foreach (var edge in edges)
        {
            if (forest.Find(edge.A) != forest.Find(edge.B))
            {
                forest.Union(edge.A, edge.B);
                mst.Add(edge);
            }
        }

        for (var i = 1; i < n && mst.Count < n - 1; i++)
        {
            if (forest.Find(0) != forest.Find(i))
            {
                forest.Union(0, i);
                mst.Add((0, i, double.PositiveInfinity));
            }
        }

        return mst;
    }

    /// <summary>Sorted heights of the single-linkage merges (the mutual reachability MST weights), unique even with ties.</summary>
    public static double[] LinkageHeights(IReadOnlyList<float[]> points, int minSamples)
    {
        ArgumentNullException.ThrowIfNull(points);
        return ExactTree(points, minSamples).Select(e => e.W).Order().ToArray();
    }

    /// <summary>Cosine distance (1 − dot product of normalized vectors).</summary>
    public static double Distance(float[] a, float[] b)
    {
        var dot = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
        }

        return Math.Max(0, 1 - dot);
    }

    private static (int[][] Ids, double[][] Distances) NearestNeighbors(IReadOnlyList<float[]> points, int k)
    {
        var n = points.Count;
        var ids = new int[n][];
        var distances = new double[n][];
        Parallel.For(0, n, i =>
        {
            var all = new (double D, int J)[n - 1];
            var m = 0;
            for (var j = 0; j < n; j++)
            {
                if (j != i)
                {
                    all[m++] = (Distance(points[i], points[j]), j);
                }
            }

            Array.Sort(all, (x, y) => x.D != y.D ? x.D.CompareTo(y.D) : x.J.CompareTo(y.J));
            ids[i] = all.Take(k).Select(e => e.J).ToArray();
            distances[i] = all.Take(k).Select(e => e.D).ToArray();
        });
        return (ids, distances);
    }

    /// <summary>scipy-style linkage rows (left, right, distance, size) from the MST edges in ascending order.</summary>
    private static (int Left, int Right, double Distance, int Size)[] SingleLinkage(List<(int A, int B, double W)> mst, int n)
    {
        // Stable, as numpy's mergesort: ties keep the discovery order.
        mst = mst.Select((e, i) => (e, i)).OrderBy(x => x.e.W).ThenBy(x => x.i).Select(x => x.e).ToList();
        var parent = Enumerable.Range(0, (2 * n) - 1).ToArray();
        var size = new int[(2 * n) - 1];
        Array.Fill(size, 1, 0, n);
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        var result = new (int, int, double, int)[n - 1];
        var next = n;
        for (var i = 0; i < mst.Count; i++)
        {
            var (a, b, w) = mst[i];
            var ra = Find(a);
            var rb = Find(b);
            result[i] = (ra, rb, w, size[ra] + size[rb]);
            parent[ra] = next;
            parent[rb] = next;
            size[next] = size[ra] + size[rb];
            next++;
        }

        return result;
    }

    private sealed record CondensedRow(int Parent, int Child, double Lambda, int ChildSize);

    /// <summary>Port of <c>condense_tree</c>.</summary>
    private static List<CondensedRow> Condense((int Left, int Right, double Distance, int Size)[] hierarchy, int n, int minClusterSize)
    {
        var root = 2 * (n - 1);
        var relabel = new int[root + 1];
        relabel[root] = n;
        var nextLabel = n + 1;
        var ignore = new bool[root + 1];
        var result = new List<CondensedRow>();

        foreach (var node in Bfs(hierarchy, root, n))
        {
            if (ignore[node] || node < n)
            {
                continue;
            }

            var (left, right, distance, _) = hierarchy[node - n];
            var lambda = distance > 0 ? 1.0 / distance : double.PositiveInfinity;
            var leftCount = left >= n ? hierarchy[left - n].Size : 1;
            var rightCount = right >= n ? hierarchy[right - n].Size : 1;

            if (leftCount >= minClusterSize && rightCount >= minClusterSize)
            {
                relabel[left] = nextLabel++;
                result.Add(new CondensedRow(relabel[node], relabel[left], lambda, leftCount));
                relabel[right] = nextLabel++;
                result.Add(new CondensedRow(relabel[node], relabel[right], lambda, rightCount));
            }
            else if (leftCount < minClusterSize && rightCount < minClusterSize)
            {
                FallOut(left);
                FallOut(right);
            }
            else if (leftCount < minClusterSize)
            {
                relabel[right] = relabel[node];
                FallOut(left);
            }
            else
            {
                relabel[left] = relabel[node];
                FallOut(right);
            }

            void FallOut(int subtree)
            {
                foreach (var sub in Bfs(hierarchy, subtree, n))
                {
                    if (sub < n)
                    {
                        result.Add(new CondensedRow(relabel[node], sub, lambda, 1));
                    }

                    ignore[sub] = true;
                }
            }
        }

        return result;
    }

    private static IEnumerable<int> Bfs((int Left, int Right, double Distance, int Size)[] hierarchy, int start, int n)
    {
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            yield return node;
            if (node >= n)
            {
                queue.Enqueue(hierarchy[node - n].Left);
                queue.Enqueue(hierarchy[node - n].Right);
            }
        }
    }

    /// <summary>Port of <c>compute_stability</c>: Σ (λ_p − λ_birth) × size over the cluster's rows.</summary>
    private static Dictionary<int, double> Stability(List<CondensedRow> tree)
    {
        var smallest = tree.Min(r => r.Parent);
        var births = new Dictionary<int, double> { [smallest] = 0 };
        foreach (var row in tree)
        {
            births[row.Child] = row.Lambda;
        }

        var stability = tree.Select(r => r.Parent).Distinct().ToDictionary(p => p, _ => 0.0);
        foreach (var row in tree)
        {
            stability[row.Parent] += (row.Lambda - births[row.Parent]) * row.ChildSize;
        }

        return stability;
    }

    /// <summary>Port of <c>get_clusters</c> (allow_single_cluster = false, epsilon = 0).</summary>
    private static HashSet<int> Select(List<CondensedRow> tree, Dictionary<int, double> stability, ClusterSelection selection)
    {
        var nodes = stability.Keys.OrderByDescending(k => k).ToList();
        nodes.RemoveAt(nodes.Count - 1); // the root is never a cluster
        var clusterTree = tree.Where(r => r.ChildSize > 1).ToList();
        var isCluster = nodes.ToDictionary(c => c, _ => true);

        if (selection == ClusterSelection.ExcessOfMass)
        {
            foreach (var node in nodes)
            {
                var subtree = clusterTree.Where(r => r.Parent == node).Sum(r => stability[r.Child]);
                if (subtree > stability[node])
                {
                    isCluster[node] = false;
                    stability[node] = subtree;
                }
                else
                {
                    foreach (var sub in ClusterTreeBfs(clusterTree, node).Where(s => s != node))
                    {
                        isCluster[sub] = false;
                    }
                }
            }
        }
        else
        {
            var parents = clusterTree.Select(r => r.Parent).ToHashSet();
            var leaves = clusterTree.Select(r => r.Child).Where(c => !parents.Contains(c)).ToHashSet();
            foreach (var c in isCluster.Keys.ToList())
            {
                isCluster[c] = leaves.Contains(c);
            }
        }

        return isCluster.Where(c => c.Value).Select(c => c.Key).ToHashSet();
    }

    private static IEnumerable<int> ClusterTreeBfs(List<CondensedRow> clusterTree, int root)
    {
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            yield return node;
            foreach (var row in clusterTree.Where(r => r.Parent == node))
            {
                queue.Enqueue(row.Child);
            }
        }
    }

    /// <summary>Port of <c>do_labelling</c>: points reach their selected cluster through the rows not cut by it.</summary>
    private static int[] Label(List<CondensedRow> tree, HashSet<int> clusters, int n)
    {
        var labels = Enumerable.Repeat(-1, n).ToArray();
        var rootCluster = tree.Min(r => r.Parent);
        var maxNode = tree.Max(r => Math.Max(r.Parent, r.Child));
        var unionFind = new UnionFind(maxNode + 1);
        foreach (var row in tree.Where(r => !clusters.Contains(r.Child)))
        {
            unionFind.Union(row.Parent, row.Child);
        }

        var map = clusters.Order().Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        for (var p = 0; p < n; p++)
        {
            var cluster = unionFind.Find(p);
            labels[p] = cluster > rootCluster && map.TryGetValue(cluster, out var label) ? label : -1;
        }

        return labels;
    }

    /// <summary>Union-find where <see cref="Union"/> hangs the second set under the first one's root.</summary>
    private sealed class UnionFind(int size)
    {
        private readonly int[] _parent = Enumerable.Range(0, size).ToArray();

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }

            return x;
        }

        /// <summary>
        /// Joins the sets; the root of <paramref name="a"/> stays the root. The condensed-tree rows come top-down, so a
        /// point ends under the highest cluster not cut by a selected one (as <c>do_labelling</c> with its ranks).
        /// </summary>
        public void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
            {
                _parent[rb] = ra;
            }
        }
    }
}
