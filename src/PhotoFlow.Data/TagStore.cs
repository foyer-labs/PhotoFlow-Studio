// SPDX-License-Identifier: Apache-2.0

using Dapper;
using Microsoft.Data.Sqlite;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Data;

/// <summary>Tags (spec §8, §9). Each method runs in one transaction; the I5 rules are written in the SQL.</summary>
internal sealed class TagStore(SqliteCatalog catalog) : ITagStore
{
    private const string TagColumns = """
        t.Id, t.Key, t.Category, t.Source, t.Reviewed, t.Blacklisted,
        (SELECT Label FROM TagLabels l WHERE l.TagId = t.Id AND l.Language = 'it') AS Italian
        """;

    public Task SeedVocabularyAsync(IReadOnlyList<VocabularyEntry> entries, TagSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            var existing = (await c.QueryAsync<string>(new CommandDefinition("SELECT Key FROM Tags;", transaction: transaction, cancellationToken: ct)).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var entry in entries.Where(e => !existing.Contains(e.Key)))
            {
                var id = await InsertAsync(c, transaction, entry.Key, source, entry.Category, entry.Italian, reviewed: false, ct).ConfigureAwait(false);
                if (entry.Blacklisted)
                {
                    await c.ExecuteAsync(new CommandDefinition("UPDATE Tags SET Blacklisted = 1 WHERE Id = @id;", new { id }, transaction, cancellationToken: ct)).ConfigureAwait(false);
                }
            }

            transaction.Commit();
        }, ct);
    }

    public async Task<Tag?> FindAsync(string key, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<TagRow>(new CommandDefinition(
            $"SELECT {TagColumns}, 0 AS PhotoCount FROM Tags t WHERE t.Key = @key;", new { key }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => r.ToTag()).FirstOrDefault();

    public Task<long> EnsureAsync(string key, TagSource source, string? category, string? italian, bool reviewed, CancellationToken ct) =>
        catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            var id = await EnsureAsync(c, transaction, key, source, category, italian, reviewed, ct).ConfigureAwait(false);
            transaction.Commit();
            return id;
        }, ct);

    public async Task<IReadOnlyList<Tag>> ListAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<TagRow>(new CommandDefinition(
            $"""
            SELECT {TagColumns},
                   (SELECT COUNT(*) FROM AssetTags a WHERE a.TagId = t.Id AND a.State = 'Active') AS PhotoCount
            FROM Tags t ORDER BY t.Key;
            """, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => r.ToTag()).ToList();

    public Task ReplaceAutomaticAsync(long assetId, TagSource source, string? modelVersion, IReadOnlyList<(string Key, double? Confidence)> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (source == TagSource.Manual)
        {
            throw new ArgumentException("Manual tags are added by the user only.", nameof(source));
        }

        return catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            var ids = new Dictionary<long, double?>();
            foreach (var (key, confidence) in tags)
            {
                ids[await EnsureAsync(c, transaction, key, source, null, null, false, ct).ConfigureAwait(false)] = confidence;
            }

            // The automatic, active tags of this source that the analysis no longer finds go away.
            var current = await c.QueryAsync<long>(new CommandDefinition(
                "SELECT TagId FROM AssetTags WHERE AssetId = @assetId AND Source = @source AND State = 'Active';",
                new { assetId, source = source.ToString() }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            foreach (var stale in current.Where(id => !ids.ContainsKey(id)))
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM AssetTags WHERE AssetId = @assetId AND TagId = @stale AND Source = @source AND State = 'Active';",
                    new { assetId, stale, source = source.ToString() }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            }

            // New or refreshed: never over a manual or a suppressed assignment (I5), nor over another source's tag.
            foreach (var (tagId, confidence) in ids)
            {
                await c.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO AssetTags (AssetId, TagId, Source, Confidence, ModelVersion, State, UpdatedAtMs)
                    VALUES (@assetId, @tagId, @source, @confidence, @modelVersion, 'Active', @now)
                    ON CONFLICT (AssetId, TagId) DO UPDATE SET Confidence = excluded.Confidence, ModelVersion = excluded.ModelVersion, UpdatedAtMs = excluded.UpdatedAtMs
                    WHERE AssetTags.Source = excluded.Source AND AssetTags.State = 'Active';
                    """,
                    new { assetId, tagId, source = source.ToString(), confidence, modelVersion, now = Db.Now() }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            }

            transaction.Commit();
        }, ct);
    }

    public Task AddManualAsync(long assetId, string key, CancellationToken ct) =>
        catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            var tagId = await EnsureAsync(c, transaction, key, TagSource.Manual, null, null, true, ct).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO AssetTags (AssetId, TagId, Source, Confidence, ModelVersion, State, UpdatedAtMs)
                VALUES (@assetId, @tagId, 'Manual', NULL, NULL, 'Active', @now)
                ON CONFLICT (AssetId, TagId) DO UPDATE SET Source = 'Manual', Confidence = NULL, ModelVersion = NULL, State = 'Active', UpdatedAtMs = excluded.UpdatedAtMs;
                """,
                new { assetId, tagId, now = Db.Now() }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            transaction.Commit();
        }, ct);

    public Task SuppressAsync(long assetId, long tagId, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO AssetTags (AssetId, TagId, Source, Confidence, ModelVersion, State, UpdatedAtMs)
            VALUES (@assetId, @tagId, 'Manual', NULL, NULL, 'Suppressed', @now)
            ON CONFLICT (AssetId, TagId) DO UPDATE SET State = 'Suppressed', UpdatedAtMs = excluded.UpdatedAtMs;
            """,
            new { assetId, tagId, now = Db.Now() }, cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<AssetTagInfo>> GetForAssetAsync(long assetId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<AssignmentRow>(new CommandDefinition(
            $"""
            SELECT {TagColumns}, 0 AS PhotoCount, a.AssetId, a.Source AS AssignmentSource, a.Confidence, a.ModelVersion, a.State
            FROM AssetTags a JOIN Tags t ON t.Id = a.TagId WHERE a.AssetId = @assetId ORDER BY t.Key;
            """, new { assetId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new AssetTagInfo(r.ToTag(), new AssetTag(r.AssetId, r.Id, Db.Parse<TagSource>(r.AssignmentSource), r.Confidence, r.ModelVersion, Db.Parse<AssetTagState>(r.State))))
        .ToList();

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<Tag>>> GetActiveForAssetsAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var result = assetIds.Distinct().ToDictionary(id => id, _ => (IReadOnlyList<Tag>)[]);
        foreach (var chunk in assetIds.Distinct().Chunk(500))
        {
            var rows = await catalog.RunAsync(c => c.QueryAsync<AssignmentRow>(new CommandDefinition(
                $"""
                SELECT {TagColumns}, 0 AS PhotoCount, a.AssetId, a.Source AS AssignmentSource, a.Confidence, a.ModelVersion, a.State
                FROM AssetTags a JOIN Tags t ON t.Id = a.TagId WHERE a.AssetId IN @chunk AND a.State = 'Active' ORDER BY t.Key;
                """, new { chunk }, cancellationToken: ct)), ct).ConfigureAwait(false);
            foreach (var group in rows.GroupBy(r => r.AssetId))
            {
                result[group.Key] = group.Select(r => r.ToTag()).ToList();
            }
        }

        return result;
    }

    public Task UpdateAsync(long tagId, string? italian, bool reviewed, string? category, bool blacklisted, CancellationToken ct) =>
        catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE Tags SET Reviewed = @reviewed, Category = @category, Blacklisted = @blacklisted WHERE Id = @tagId;",
                new { tagId, reviewed, category, blacklisted }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            await SetItalianAsync(c, transaction, tagId, italian, ct).ConfigureAwait(false);
            transaction.Commit();
        }, ct);

    public async Task<IReadOnlyList<(long AssetId, double? Confidence)>> ListExamplesAsync(long tagId, int count, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<(long AssetId, double? Confidence)>(new CommandDefinition(
            "SELECT AssetId, Confidence FROM AssetTags WHERE TagId = @tagId AND State = 'Active' ORDER BY Confidence IS NULL, Confidence LIMIT @count;",
            new { tagId, count }, cancellationToken: ct)), ct).ConfigureAwait(false)).ToList();

    public Task<int> CountTaggedAssetsAsync(CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT AssetId) FROM AssetTags WHERE State = 'Active' AND Source IN ('RamPlus', 'ClipZeroShot');", cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<(Tag Tag, CustomTag Custom)>> ListCustomAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<CustomRow>(new CommandDefinition(
            $"""
            SELECT {TagColumns},
                   (SELECT COUNT(*) FROM AssetTags a WHERE a.TagId = t.Id AND a.State = 'Active') AS PhotoCount,
                   ct.ClipDescription, ct.Threshold, ct.Margin
            FROM CustomTags ct JOIN Tags t ON t.Id = ct.TagId ORDER BY t.Key;
            """, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => (r.ToTag(), new CustomTag(r.Id, r.ClipDescription, r.Threshold, r.Margin))).ToList();

    public Task<long> SaveCustomAsync(string key, string italian, string? category, string clipDescription, double threshold, double margin, CancellationToken ct) =>
        catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            var tagId = await EnsureAsync(c, transaction, key, TagSource.ClipZeroShot, category, italian, true, ct).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE Tags SET Category = @category, Reviewed = 1 WHERE Id = @tagId;", new { tagId, category }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            await SetItalianAsync(c, transaction, tagId, italian, ct).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO CustomTags (TagId, ClipDescription, Threshold, Margin) VALUES (@tagId, @clipDescription, @threshold, @margin)
                ON CONFLICT (TagId) DO UPDATE SET ClipDescription = excluded.ClipDescription, Threshold = excluded.Threshold, Margin = excluded.Margin;
                """,
                new { tagId, clipDescription, threshold, margin }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            transaction.Commit();
            return tagId;
        }, ct);

    private static async Task<long> EnsureAsync(SqliteConnection c, SqliteTransaction transaction, string key, TagSource source, string? category, string? italian, bool reviewed, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var id = await c.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT Id FROM Tags WHERE Key = @key;", new { key }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        return id ?? await InsertAsync(c, transaction, key, source, category, italian, reviewed, ct).ConfigureAwait(false);
    }

    private static async Task<long> InsertAsync(SqliteConnection c, SqliteTransaction transaction, string key, TagSource source, string? category, string? italian, bool reviewed, CancellationToken ct)
    {
        var id = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO Tags (Key, Category, Source, Reviewed, Blacklisted) VALUES (@key, @category, @source, @reviewed, 0) RETURNING Id;",
            new { key, category, source = source.ToString(), reviewed }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        await SetItalianAsync(c, transaction, id, italian, ct).ConfigureAwait(false);
        return id;
    }

    private static Task SetItalianAsync(SqliteConnection c, SqliteTransaction transaction, long tagId, string? italian, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(italian)
            ? c.ExecuteAsync(new CommandDefinition("DELETE FROM TagLabels WHERE TagId = @tagId AND Language = 'it';", new { tagId }, transaction, cancellationToken: ct))
            : c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO TagLabels (TagId, Language, Label) VALUES (@tagId, 'it', @italian)
                ON CONFLICT (TagId, Language) DO UPDATE SET Label = excluded.Label;
                """,
                new { tagId, italian = italian.Trim() }, transaction, cancellationToken: ct));

    private class TagRow
    {
        public long Id { get; set; }

        public string Key { get; set; } = string.Empty;

        public string? Category { get; set; }

        public string Source { get; set; } = string.Empty;

        public bool Reviewed { get; set; }

        public bool Blacklisted { get; set; }

        public string? Italian { get; set; }

        public int PhotoCount { get; set; }

        public Tag ToTag() => new(Id, Key, Category, Db.Parse<TagSource>(Source), Reviewed, Blacklisted) { Italian = Italian, PhotoCount = PhotoCount };
    }

    private sealed class AssignmentRow : TagRow
    {
        public long AssetId { get; set; }

        public string AssignmentSource { get; set; } = string.Empty;

        public double? Confidence { get; set; }

        public string? ModelVersion { get; set; }

        public string State { get; set; } = string.Empty;
    }

    private sealed class CustomRow : TagRow
    {
        public string ClipDescription { get; set; } = string.Empty;

        public double Threshold { get; set; }

        public double Margin { get; set; }
    }
}
