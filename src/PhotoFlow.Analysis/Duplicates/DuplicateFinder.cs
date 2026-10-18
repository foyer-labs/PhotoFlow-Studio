// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;

namespace PhotoFlow.Analysis.Duplicates;

/// <summary>Result of a duplicate search (board 4f/4g): groups to look at and photos checked.</summary>
public sealed record DuplicateScan(IReadOnlyList<DuplicateGroup> Exact, IReadOnlyList<DuplicateGroup> Near, int Checked, DateTimeOffset At);

/// <summary>
/// Duplicates (spec §12 view 7, P5; decisions F3-5, F3-6): exact copies are the same content twice in a library; near
/// duplicates are different photos whose CLIP embeddings have cosine ≥ <see cref="NearMinCosine"/> (exact scan of all
/// pairs, parallel) confirmed by a perceptual hash of the thumbnails within <see cref="MaxHashDistance"/> bits. The RAW
/// and JPEG of a pair are one photo; groups the user marked "not duplicates" are not proposed again. Nothing is moved
/// here.
/// </summary>
public sealed class DuplicateFinder(ICatalog catalog, IThumbnailStore thumbnails, IImageOperations imageOperations)
{
    public const double NearMinCosine = 0.95;
    public const int MaxHashDistance = 10;

    public async Task<DuplicateScan> ScanAsync(string? embeddingModelVersion, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var exclusions = await catalog.Duplicates.ListExclusionsAsync(ct).ConfigureAwait(false);
        var exact = new List<DuplicateGroup>();
        foreach (var copies in await catalog.Duplicates.ListExactCopiesAsync(ct).ConfigureAwait(false))
        {
            var key = "exact:" + string.Join(',', copies.Select(c => c.Id).Order());
            if (!exclusions.Contains(key))
            {
                var members = copies.Select(c => new DuplicateMember(c.AssetId, c)).ToList();
                exact.Add(new DuplicateGroup(key, DuplicateKind.Exact, members, 1, await RecommendAsync(members, ct).ConfigureAwait(false)));
            }
        }

        var near = new List<DuplicateGroup>();
        var checkedPhotos = 0;
        if (embeddingModelVersion is not null)
        {
            var ids = await catalog.Analysis.ListEmbeddedAssetsAsync(embeddingModelVersion, ct).ConfigureAwait(false);
            checkedPhotos = ids.Count;
            var twins = new HashSet<(long, long)>();
            foreach (var id in ids)
            {
                if (await catalog.Assets.GetTwinAsync(id, ct).ConfigureAwait(false) is { } twin)
                {
                    twins.Add((Math.Min(id, twin), Math.Max(id, twin)));
                }
            }

            var vectors = await catalog.Analysis.GetEmbeddingsAsync(ids, embeddingModelVersion, ct).ConfigureAwait(false);
            var pairs = SimilarPairs(ids.Where(vectors.ContainsKey).ToList(), vectors, NearMinCosine, progress, ct)
                .Where(p => !twins.Contains((Math.Min(p.A, p.B), Math.Max(p.A, p.B))))
                .ToList();
            var hashes = await HashesAsync(pairs.SelectMany(p => new[] { p.A, p.B }).Distinct().ToList(), ct).ConfigureAwait(false);
            var confirmed = pairs.Where(p => hashes.TryGetValue(p.A, out var a) && hashes.TryGetValue(p.B, out var b) && BitOperations.PopCount(a ^ b) <= MaxHashDistance).ToList();

            foreach (var component in Components(confirmed))
            {
                var key = "near:" + string.Join(',', component.Order());
                if (exclusions.Contains(key))
                {
                    continue;
                }

                var members = new List<DuplicateMember>();
                foreach (var assetId in component.Order())
                {
                    if ((await catalog.Assets.GetLocationsAsync(assetId, ct).ConfigureAwait(false)).FirstOrDefault(l => !IsSetAside(l.RelativePath)) is { } location)
                    {
                        members.Add(new DuplicateMember(assetId, location));
                    }
                }

                if (members.Count >= 2)
                {
                    var similarity = confirmed.Where(p => component.Contains(p.A)).Select(p => p.Cosine).DefaultIfEmpty(NearMinCosine).Max();
                    near.Add(new DuplicateGroup(key, DuplicateKind.Near, members, similarity, await RecommendAsync(members, ct).ConfigureAwait(false)));
                }
            }
        }

        return new DuplicateScan(exact, [.. near.OrderByDescending(g => g.Similarity)], checkedPhotos, DateTimeOffset.UtcNow);
    }

    /// <summary>All pairs with cosine ≥ <paramref name="minCosine"/> (vectors L2-normalized), compared exactly in parallel.</summary>
    public static IReadOnlyList<(long A, long B, double Cosine)> SimilarPairs(IReadOnlyList<long> ids, IReadOnlyDictionary<long, float[]> vectors, double minCosine,
        IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(vectors);
        var matrix = ids.Select(id => vectors[id]).ToArray();
        var found = new ConcurrentBag<(long, long, double)>();
        var done = 0;
        Parallel.For(0, matrix.Length, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var a = matrix[i];
            for (var j = i + 1; j < matrix.Length; j++)
            {
                var cosine = Dot(a, matrix[j]);
                if (cosine >= minCosine)
                {
                    found.Add((ids[i], ids[j], cosine));
                }
            }

            var count = Interlocked.Increment(ref done);
            if (count % 1000 == 0)
            {
                progress?.Report((count, matrix.Length));
            }
        });
        return [.. found];
    }

    /// <summary>64-bit difference hash: 9×8 grayscale, one bit per horizontal neighbour comparison.</summary>
    public static ulong DifferenceHash(RgbImage image, IImageOperations imageOperations)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(imageOperations);
        var small = imageOperations.Resize(image, 9, 8, ResampleMode.Bilinear);
        var pixels = small.Pixels.Span;
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++, bit++)
            {
                if (Gray(pixels, (y * 9) + x) > Gray(pixels, (y * 9) + x + 1))
                {
                    hash |= 1UL << bit;
                }
            }
        }

        return hash;

        static int Gray(ReadOnlySpan<byte> p, int i) => (299 * p[i * 3]) + (587 * p[(i * 3) + 1]) + (114 * p[(i * 3) + 2]);
    }

    /// <summary>The copy to keep: RAW, then the larger picture, then the one with manual tags or a rating, then the oldest.</summary>
    private async Task<int> RecommendAsync(IReadOnlyList<DuplicateMember> members, CancellationToken ct)
    {
        var scored = new List<(int Index, bool Raw, long Pixels, bool Edited, DateTimeOffset Seen)>();
        for (var i = 0; i < members.Count; i++)
        {
            var asset = await catalog.Assets.GetAsync(members[i].AssetId, ct).ConfigureAwait(false);
            var manualTags = (await catalog.Tags.GetForAssetAsync(members[i].AssetId, ct).ConfigureAwait(false)).Any(t => t.Assignment.Source == TagSource.Manual);
            var rated = (await catalog.Decisions.GetAsync(members[i].AssetId, ct).ConfigureAwait(false))?.Rating is > 0;
            scored.Add((i, asset?.Handler == "raw", (long)(asset?.Width ?? 0) * (asset?.Height ?? 0), manualTags || rated, members[i].Location.LastSeenAt));
        }

        return scored.OrderByDescending(s => s.Raw).ThenByDescending(s => s.Pixels).ThenByDescending(s => s.Edited)
            .ThenBy(s => members[s.Index].Location.RelativePath.Length).ThenBy(s => members[s.Index].Location.RelativePath, StringComparer.Ordinal).First().Index;
    }

    private async Task<Dictionary<long, ulong>> HashesAsync(IReadOnlyList<long> assetIds, CancellationToken ct)
    {
        var hashes = (await catalog.Duplicates.GetHashesAsync(assetIds, ct).ConfigureAwait(false)).ToDictionary(p => p.Key, p => p.Value);
        foreach (var assetId in assetIds.Where(id => !hashes.ContainsKey(id)))
        {
            var asset = await catalog.Assets.GetAsync(assetId, ct).ConfigureAwait(false);
            var path = asset is null ? null : new[] { asset.FullHash, asset.QuickHash }.OfType<string>().Select(thumbnails.PathFor).FirstOrDefault(File.Exists);
            if (path is null)
            {
                continue; // no thumbnail: no confirmation, the pair is not proposed
            }

            await using var stream = File.OpenRead(path);
            var hash = DifferenceHash(await imageOperations.DecodeAsync(stream, ct).ConfigureAwait(false), imageOperations);
            await catalog.Duplicates.SetHashAsync(assetId, hash, ct).ConfigureAwait(false);
            hashes[assetId] = hash;
        }

        return hashes;
    }

    private static List<HashSet<long>> Components(IReadOnlyList<(long A, long B, double Cosine)> pairs)
    {
        var parent = new Dictionary<long, long>();
        long Find(long x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        foreach (var (a, b, _) in pairs)
        {
            parent.TryAdd(a, a);
            parent.TryAdd(b, b);
            var (ra, rb) = (Find(a), Find(b));
            if (ra != rb)
            {
                parent[ra] = rb;
            }
        }

        return parent.Keys.GroupBy(Find).Select(g => g.ToHashSet()).ToList();
    }

    private static double Dot(float[] a, float[] b)
    {
        var sum = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            sum += new Vector<float>(a, i) * new Vector<float>(b, i);
        }

        var total = Vector.Sum(sum);
        for (; i < a.Length; i++)
        {
            total += a[i] * b[i];
        }

        return total;
    }

    /// <summary>Files moved aside on request (<c>_duplicati</c>, <c>_scarti</c>) are not proposed again.</summary>
    public static bool IsSetAside(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var first = relativePath.Split('/', '\\')[0];
        return first.Equals("_duplicati", StringComparison.OrdinalIgnoreCase) || first.Equals("_scarti", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Text for a file size.</summary>
    public static string Megabytes(long bytes) => (bytes / 1e6).ToString("0.0", CultureInfo.CurrentCulture) + " MB";
}
