// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Dapper;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Search;

namespace PhotoFlow.Data;

/// <summary>
/// Search on SQLite (decision F3-4): exact filters in SQL, words in the FTS5 table <c>SearchText</c> (updated photo by
/// photo from the changes marked by triggers), meaning and similarity by an exact scan of the image
/// embeddings kept in memory (60,000 × 512 floats = 120 MB, a few tens of milliseconds with SIMD). The RAW of a
/// RAW+JPEG pair is not a separate result: its JPEG carries tags and faces.
/// </summary>
internal sealed class SqliteSearchIndex(SqliteCatalog catalog) : ISearchIndex
{
    /// <summary>Free text by meaning: a photo is kept when its cosine with the text reaches this…</summary>
    public const double MeaningMinScore = 0.20;

    /// <summary>…and is within this distance of the best photo (CLIP cosines of matching photos are close to each other).</summary>
    public const double MeaningWindow = 0.06;

    /// <summary>"Trova simili": cosine between image embeddings.</summary>
    public const double SimilarMinScore = 0.80;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private VectorCache? _vectors;

    public async Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var clock = Stopwatch.StartNew();
        await EnsureTextIndexAsync(ct).ConfigureAwait(false);
        var candidates = await CandidatesAsync(query, ct).ConfigureAwait(false);
        var words = Words(query.Text);
        IReadOnlyList<SearchHit> ordered;
        var byMeaning = false;
        var vectors = query.EmbeddingModelVersion is { } model && (query.TextEmbedding is not null || query.SimilarTo is not null)
            ? await VectorsAsync(model, ct).ConfigureAwait(false)
            : null;

        if (query.SimilarTo is { } similar && vectors is not null && vectors.IndexOf(similar) is { } reference)
        {
            byMeaning = true;
            var probe = vectors.Vector(reference).ToArray();
            ordered = Score(candidates, vectors, probe).Where(h => h.AssetId != similar && h.Score >= SimilarMinScore).OrderByDescending(h => h.Score).ToList();
        }
        else if (query.Text is not null && (words.Count > 0 || query.TextEmbedding is not null))
        {
            var matched = words.Count > 0 ? await MatchWordsAsync(words, ct).ConfigureAwait(false) : [];
            if (query.TextEmbedding is { } text && vectors is not null)
            {
                byMeaning = true;
                var scores = Score(candidates, vectors, text).ToDictionary(h => h.AssetId, h => h.Score);
                var best = scores.Count == 0 ? 0 : scores.Values.Max();
                var cut = Math.Max(MeaningMinScore, best - MeaningWindow);
                ordered = candidates.Where(id => matched.Contains(id) || (scores.TryGetValue(id, out var s) && s >= cut))
                    .Select(id => new SearchHit(id, scores.GetValueOrDefault(id, double.NegativeInfinity)))
                    .OrderByDescending(h => h.Score).ThenByDescending(h => h.AssetId).ToList();
            }
            else
            {
                ordered = candidates.Where(matched.Contains).Select(id => new SearchHit(id, 0)).ToList();
            }
        }
        else
        {
            ordered = candidates.Select(id => new SearchHit(id, 0)).ToList();
        }

        return new SearchResults(ordered.Take(query.Limit).ToList(), ordered.Count, byMeaning, clock.Elapsed);
    }

    public async Task<IReadOnlyDictionary<long, int>> CountTagsAsync(SearchQuery query, SearchQuery? excluding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var ids = await ResultIdsAsync(query, ct).ConfigureAwait(false);
        if (excluding is not null)
        {
            var leave = (await ResultIdsAsync(excluding, ct).ConfigureAwait(false)).ToHashSet();
            ids = ids.Where(id => !leave.Contains(id)).ToList();
        }

        return (await CountOverAsync(ids, persons: false, ct).ConfigureAwait(false)).Tags;
    }

    public async Task<IReadOnlyDictionary<long, int>> CountPersonsAsync(SearchQuery query, CancellationToken ct) =>
        (await CountFacetsAsync(query, ct).ConfigureAwait(false)).Persons;

    public async Task<FacetCounts> CountFacetsAsync(SearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await CountOverAsync(await ResultIdsAsync(query, ct).ConfigureAwait(false), persons: true, ct).ConfigureAwait(false);
    }

    /// <summary>Every photo of the result (no limit), for the facets.</summary>
    private async Task<List<long>> ResultIdsAsync(SearchQuery query, CancellationToken ct) =>
        [.. (await SearchAsync(query with { Limit = int.MaxValue }, ct).ConfigureAwait(false)).Hits.Select(h => h.AssetId)];

    /// <summary>
    /// The counts over the photos <paramref name="ids"/>, held in a temporary table of this connection. Both counts read
    /// only an index already in tag (person) order (migration 0018); blacklisted tags are left out afterwards.
    /// </summary>
    private Task<FacetCounts> CountOverAsync(IReadOnlyList<long> ids, bool persons, CancellationToken ct) =>
        catalog.RunAsync(async c =>
        {
            await c.ExecuteAsync(new CommandDefinition("CREATE TEMP TABLE IF NOT EXISTS FacetIds (Id INTEGER PRIMARY KEY);", cancellationToken: ct)).ConfigureAwait(false);
            await using var transaction = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM temp.FacetIds;", transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
            // One statement for any number of photos.
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO temp.FacetIds (Id) SELECT value FROM json_each(@ids);",
                new { ids = "[" + string.Join(',', ids.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]" }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            var blacklisted = (await c.QueryAsync<long>(new CommandDefinition("SELECT Id FROM Tags WHERE Blacklisted = 1;", transaction: transaction, cancellationToken: ct))
                .ConfigureAwait(false)).ToHashSet();
            var tags = (await c.QueryAsync<(long Id, int Count)>(new CommandDefinition("""
                SELECT x.TagId, COUNT(*) FROM AssetTags x INDEXED BY IX_AssetTags_Tag
                WHERE x.State = 'Active' AND x.AssetId IN (SELECT Id FROM temp.FacetIds) GROUP BY x.TagId;
                """, transaction: transaction, cancellationToken: ct)).ConfigureAwait(false))
                .Where(r => !blacklisted.Contains(r.Id)).ToDictionary(r => r.Id, r => r.Count);
            var people = persons
                ? (await c.QueryAsync<(long Id, int Count)>(new CommandDefinition("""
                    SELECT f.PersonId, COUNT(DISTINCT f.AssetId) FROM Faces f INDEXED BY IX_Faces_Person
                    WHERE f.PersonId IS NOT NULL AND f.AssetId IN (SELECT Id FROM temp.FacetIds) GROUP BY f.PersonId;
                    """, transaction: transaction, cancellationToken: ct)).ConfigureAwait(false)).ToDictionary(r => r.Id, r => r.Count)
                : new Dictionary<long, int>();
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new FacetCounts(tags, people);
        }, ct);

    public async Task<(int Embedded, int Total)> CoverageAsync(string embeddingModelVersion, CancellationToken ct) =>
        await catalog.RunAsync(async c =>
        {
            var embedded = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM Embeddings WHERE ModelVersion = @embeddingModelVersion;", new { embeddingModelVersion }, cancellationToken: ct)).ConfigureAwait(false);
            var total = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM Assets a WHERE a.State = 'Active' AND NOT EXISTS (SELECT 1 FROM AssetPairs p WHERE p.RawAssetId = a.Id);", cancellationToken: ct)).ConfigureAwait(false);
            return (embedded, total);
        }, ct).ConfigureAwait(false);

    /// <summary>Photos matching the exact filters, newest first.</summary>
    private async Task<List<long>> CandidatesAsync(SearchQuery query, CancellationToken ct)
    {
        var sql = new StringBuilder("""
            SELECT a.Id FROM Assets a LEFT JOIN Decisions d ON d.AssetId = a.Id
            WHERE a.State = 'Active' AND NOT EXISTS (SELECT 1 FROM AssetPairs p WHERE p.RawAssetId = a.Id)
            """);
        var parameters = new DynamicParameters();
        for (var i = 0; i < query.PersonIds.Count; i++)
        {
            sql.Append(CultureInfo.InvariantCulture, $" AND a.Id IN (SELECT f.AssetId FROM Faces f WHERE f.PersonId = @p{i})");
            parameters.Add($"p{i}", query.PersonIds[i]);
        }

        for (var i = 0; i < query.TagIds.Count; i++)
        {
            sql.Append(CultureInfo.InvariantCulture, $" AND a.Id IN (SELECT x.AssetId FROM AssetTags x WHERE x.TagId = @t{i} AND x.State = 'Active')");
            parameters.Add($"t{i}", query.TagIds[i]);
        }

        foreach (var (group, g) in (query.TagGroups ?? []).Where(group => group.Count > 0).Select((group, g) => (group, g)))
        {
            sql.Append(CultureInfo.InvariantCulture, $" AND a.Id IN (SELECT x.AssetId FROM AssetTags x WHERE x.TagId IN @g{g} AND x.State = 'Active')");
            parameters.Add($"g{g}", group.ToArray());
        }

        if (query.From is { } from)
        {
            sql.Append(" AND a.CapturedAtMs >= @from");
            parameters.Add("from", from.ToUnixTimeMilliseconds());
        }

        if (query.To is { } to)
        {
            sql.Append(" AND a.CapturedAtMs < @to");
            parameters.Add("to", to.ToUnixTimeMilliseconds());
        }

        if (query.Camera is { } camera)
        {
            sql.Append(" AND a.CameraModel = @camera");
            parameters.Add("camera", camera);
        }

        if (query.MinRating is { } rating)
        {
            sql.Append(" AND d.Rating >= @rating");
            parameters.Add("rating", rating);
        }

        if (query.Outcome is { } outcome)
        {
            sql.Append(" AND COALESCE(d.UserOutcome, d.AutomaticOutcome) = @outcome");
            parameters.Add("outcome", outcome.ToString());
        }

        sql.Append(" ORDER BY a.CapturedAtMs DESC, a.Id DESC;");
        return (await catalog.RunAsync(c => c.QueryAsync<long>(new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct)), ct).ConfigureAwait(false)).ToList();
    }

    private async Task<HashSet<long>> MatchWordsAsync(IReadOnlyList<string> words, CancellationToken ct)
    {
        var match = string.Join(' ', words.Select(w => $"\"{w.Replace("\"", "\"\"", StringComparison.Ordinal)}\"*"));
        return (await catalog.RunAsync(c => c.QueryAsync<long>(new CommandDefinition(
            "SELECT rowid FROM SearchText WHERE SearchText MATCH @match;", new { match }, cancellationToken: ct)), ct).ConfigureAwait(false)).ToHashSet();
    }

    /// <summary>Letters and digits of the text, as words of at least two characters.</summary>
    public static IReadOnlyList<string> Words(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : new string(text.Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static IEnumerable<SearchHit> Score(IReadOnlyList<long> candidates, VectorCache vectors, ReadOnlySpan<float> probe)
    {
        var hits = new List<SearchHit>(candidates.Count);
        foreach (var id in candidates)
        {
            if (vectors.IndexOf(id) is { } index)
            {
                hits.Add(new SearchHit(id, Dot(vectors.Vector(index), probe)));
            }
        }

        return hits;
    }

    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            sum += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        }

        var total = Vector.Sum(sum);
        for (; i < a.Length; i++)
        {
            total += a[i] * b[i];
        }

        return total;
    }

    /// <summary>The image embeddings of a model in one array, reloaded when they change.</summary>
    private async Task<VectorCache> VectorsAsync(string model, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stamp = await catalog.RunAsync(c => c.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT COUNT(*) || ':' || TOTAL(AssetId) FROM Embeddings WHERE ModelVersion = @model;", new { model }, cancellationToken: ct)), ct).ConfigureAwait(false) ?? string.Empty;
            if (_vectors is { } cached && cached.Model == model && cached.Stamp == stamp)
            {
                return cached;
            }

            var rows = (await catalog.RunAsync(c => c.QueryAsync<(long AssetId, byte[] Vector)>(new CommandDefinition(
                "SELECT AssetId, Vector FROM Embeddings WHERE ModelVersion = @model;", new { model }, cancellationToken: ct)), ct).ConfigureAwait(false)).ToList();
            var dimensions = rows.Count == 0 ? 0 : rows[0].Vector.Length / sizeof(float);
            var data = new float[rows.Count * dimensions];
            var index = new Dictionary<long, int>(rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                MemoryMarshal.Cast<byte, float>(rows[i].Vector).CopyTo(data.AsSpan(i * dimensions, dimensions));
                index[rows[i].AssetId] = i;
            }

            _vectors = new VectorCache(model, stamp, dimensions, data, index);
            return _vectors;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The searchable text of the photos selected by <c>{0}</c> (file names, tag labels in both languages, people).</summary>
    private const string ContentSql = """
        INSERT INTO SearchText (rowid, Content)
        SELECT a.Id,
               IFNULL((SELECT group_concat(l.RelativePath, ' ') FROM AssetLocations l WHERE l.AssetId = a.Id), '') || ' ' ||
               IFNULL((SELECT group_concat(CASE WHEN instr(t.Key, ':') > 0 THEN substr(t.Key, instr(t.Key, ':') + 1) ELSE t.Key END || ' ' || IFNULL(tl.Label, ''), ' ')
                       FROM AssetTags x JOIN Tags t ON t.Id = x.TagId LEFT JOIN TagLabels tl ON tl.TagId = t.Id AND tl.Language = 'it'
                       WHERE x.AssetId = a.Id AND x.State = 'Active' AND t.Blacklisted = 0), '') || ' ' ||
               IFNULL((SELECT group_concat(p.Name, ' ') FROM Faces f JOIN Persons p ON p.Id = f.PersonId
                       WHERE f.AssetId = a.Id AND p.Name IS NOT NULL AND p.Ignored = 0), '') || ' ' ||
               IFNULL((SELECT group_concat(IFNULL((SELECT group_concat(j.value, ' ') FROM json_each(e.DriversJson) j), '') || ' ' || IFNULL(e.Team, '') || ' ' || IFNULL(e.Car, ''), ' ')
                       FROM VehicleNumbers v JOIN JobItems ji ON ji.AssetId = v.AssetId JOIN JobEntryLists jl ON jl.JobId = ji.JobId
                       JOIN Entries e ON e.ListId = jl.ListId AND e.Number = v.Number
                       WHERE v.AssetId = a.Id AND v.State = 'Certain'), '') || ' ' ||
               IFNULL((SELECT group_concat(cp.Text, ' ') FROM Captions cp WHERE cp.AssetId = a.Id), '')
        FROM Assets a
        WHERE a.State = 'Active' AND NOT EXISTS (SELECT 1 FROM AssetPairs p WHERE p.RawAssetId = a.Id) AND {0};
        """;

    /// <summary>
    /// Brings the full-text table up to date: built in full the first time, then only the photos marked in
    /// <c>SearchDirty</c> by the triggers of migration 0012 (in full again when most of the catalog changed).
    /// </summary>
    private async Task EnsureTextIndexAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await catalog.RunAsync(async c =>
            {
                var built = await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM SearchState WHERE Id = 1;", cancellationToken: ct)).ConfigureAwait(false) > 0;
                var dirty = (await c.QueryAsync<long>(new CommandDefinition("SELECT AssetId FROM SearchDirty;", cancellationToken: ct)).ConfigureAwait(false)).ToList();
                if (built && dirty.Count == 0)
                {
                    return;
                }

                var total = await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM Assets;", cancellationToken: ct)).ConfigureAwait(false);
                using var transaction = c.BeginTransaction();
                if (!built || dirty.Count > total / 4)
                {
                    await c.ExecuteAsync(new CommandDefinition("DELETE FROM SearchText;", transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
                    await c.ExecuteAsync(new CommandDefinition(string.Format(CultureInfo.InvariantCulture, ContentSql, "1 = 1"), transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
                    await c.ExecuteAsync(new CommandDefinition("DELETE FROM SearchDirty;", transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
                }
                else
                {
                    foreach (var chunk in dirty.Chunk(500))
                    {
                        await c.ExecuteAsync(new CommandDefinition("DELETE FROM SearchText WHERE rowid IN @chunk;", new { chunk }, transaction, cancellationToken: ct)).ConfigureAwait(false);
                        await c.ExecuteAsync(new CommandDefinition(string.Format(CultureInfo.InvariantCulture, ContentSql, "a.Id IN @chunk"), new { chunk }, transaction, cancellationToken: ct)).ConfigureAwait(false);
                        await c.ExecuteAsync(new CommandDefinition("DELETE FROM SearchDirty WHERE AssetId IN @chunk;", new { chunk }, transaction, cancellationToken: ct)).ConfigureAwait(false);
                    }
                }

                await c.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO SearchState (Id, Fingerprint) VALUES (1, 'triggers') ON CONFLICT (Id) DO UPDATE SET Fingerprint = excluded.Fingerprint;",
                    transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
                transaction.Commit();
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record VectorCache(string Model, string Stamp, int Dimensions, float[] Data, Dictionary<long, int> Index)
    {
        public int? IndexOf(long assetId) => Index.TryGetValue(assetId, out var i) ? i : null;

        public ReadOnlySpan<float> Vector(int index) => Data.AsSpan(index * Dimensions, Dimensions);
    }
}
