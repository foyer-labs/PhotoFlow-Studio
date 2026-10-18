// SPDX-License-Identifier: Apache-2.0

using Dapper;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Data;

internal sealed class DuplicateStore(SqliteCatalog catalog) : IDuplicateStore
{
    public async Task<IReadOnlyList<IReadOnlyList<AssetLocation>>> ListExactCopiesAsync(CancellationToken ct)
    {
        // Same asset (identical content) twice in a library, or two assets with the same full hash in a library; files
        // already moved into _duplicati or _scarti do not count.
        var rows = await catalog.RunAsync(c => c.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT l.Id, l.AssetId, l.LibraryId, l.RelativePath, l.LastSeenMs, COALESCE(a.FullHash, 'asset:' || a.Id) AS Content
            FROM AssetLocations l JOIN Assets a ON a.Id = l.AssetId
            WHERE a.State = 'Active' AND l.RelativePath NOT LIKE '\_duplicati%' ESCAPE '\' AND l.RelativePath NOT LIKE '\_scarti%' ESCAPE '\'
              AND (COALESCE(a.FullHash, 'asset:' || a.Id), l.LibraryId) IN (
                SELECT COALESCE(a2.FullHash, 'asset:' || a2.Id), l2.LibraryId
                FROM AssetLocations l2 JOIN Assets a2 ON a2.Id = l2.AssetId
                WHERE a2.State = 'Active' AND l2.RelativePath NOT LIKE '\_duplicati%' ESCAPE '\' AND l2.RelativePath NOT LIKE '\_scarti%' ESCAPE '\'
                GROUP BY COALESCE(a2.FullHash, 'asset:' || a2.Id), l2.LibraryId HAVING COUNT(*) > 1)
            ORDER BY Content, l.LibraryId, l.RelativePath;
            """, cancellationToken: ct)), ct).ConfigureAwait(false);
        return rows.GroupBy(r => (r.Content, r.LibraryId))
            .Select(g => (IReadOnlyList<AssetLocation>)g.Select(r => new AssetLocation(r.Id, r.AssetId, r.LibraryId, r.RelativePath, DateTimeOffset.FromUnixTimeMilliseconds(r.LastSeenMs))).ToList())
            .ToList();
    }

    public async Task<IReadOnlyDictionary<long, ulong>> GetHashesAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var result = new Dictionary<long, ulong>();
        foreach (var chunk in assetIds.Distinct().Chunk(500))
        {
            foreach (var (assetId, hash) in await catalog.RunAsync(c => c.QueryAsync<(long AssetId, long Hash)>(new CommandDefinition(
                "SELECT AssetId, Hash FROM PerceptualHashes WHERE AssetId IN @chunk;", new { chunk }, cancellationToken: ct)), ct).ConfigureAwait(false))
            {
                result[assetId] = unchecked((ulong)hash);
            }
        }

        return result;
    }

    public Task SetHashAsync(long assetId, ulong hash, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO PerceptualHashes (AssetId, Hash) VALUES (@assetId, @value) ON CONFLICT (AssetId) DO UPDATE SET Hash = excluded.Hash;",
            new { assetId, value = unchecked((long)hash) }, cancellationToken: ct)), ct);

    public async Task<IReadOnlySet<string>> ListExclusionsAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<string>(new CommandDefinition("SELECT Key FROM DuplicateExclusions;", cancellationToken: ct)), ct).ConfigureAwait(false))
        .ToHashSet(StringComparer.Ordinal);

    public Task ExcludeAsync(string key, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO DuplicateExclusions (Key, CreatedAtMs) VALUES (@key, @now) ON CONFLICT (Key) DO NOTHING;", new { key, now = Db.Now() }, cancellationToken: ct)), ct);

    private sealed class Row
    {
        public long Id { get; set; }

        public long AssetId { get; set; }

        public long LibraryId { get; set; }

        public string RelativePath { get; set; } = string.Empty;

        public long LastSeenMs { get; set; }

        public string Content { get; set; } = string.Empty;
    }
}
