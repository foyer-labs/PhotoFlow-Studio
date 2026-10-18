// SPDX-License-Identifier: Apache-2.0

using Dapper;
using PhotoFlow.Core.Develop;

namespace PhotoFlow.Data;

/// <summary>Development settings and exports of the photos (migration 0019, decision S9).</summary>
internal sealed class DevelopStore(SqliteCatalog catalog) : IDevelopStore
{
    public async Task<string?> GetSettingsAsync(long assetId, CancellationToken ct) =>
        await catalog.RunAsync(c => c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT SettingsJson FROM DevelopSettings WHERE AssetId = @assetId;", new { assetId }, cancellationToken: ct)), ct).ConfigureAwait(false);

    public Task SetSettingsAsync(long assetId, string settingsJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsJson);
        return catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO DevelopSettings (AssetId, SettingsJson, UpdatedAtMs) VALUES (@assetId, @settingsJson, @now)
            ON CONFLICT (AssetId) DO UPDATE SET SettingsJson = excluded.SettingsJson, UpdatedAtMs = excluded.UpdatedAtMs;
            """,
            new { assetId, settingsJson, now = Db.Now() }, cancellationToken: ct)), ct);
    }

    public async Task<IReadOnlySet<long>> ListDevelopedAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var result = new HashSet<long>();
        foreach (var chunk in assetIds.Distinct().Chunk(500))
        {
            var ids = await catalog.RunAsync(c => c.QueryAsync<long>(new CommandDefinition(
                "SELECT AssetId FROM DevelopSettings WHERE AssetId IN @chunk;", new { chunk }, cancellationToken: ct)), ct).ConfigureAwait(false);
            result.UnionWith(ids);
        }

        return result;
    }

    public Task AddExportAsync(ExportRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        return catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO Exports (AssetId, OutputPath, Recipe, ExportedAtMs) VALUES (@AssetId, @OutputPath, @Recipe, @ms);",
            new { record.AssetId, record.OutputPath, record.Recipe, ms = record.ExportedAt.ToUnixTimeMilliseconds() }, cancellationToken: ct)), ct);
    }

    public async Task<IReadOnlyDictionary<long, ExportRecord>> LastExportsAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var result = new Dictionary<long, ExportRecord>();
        foreach (var chunk in assetIds.Distinct().Chunk(500))
        {
            var rows = await catalog.RunAsync(c => c.QueryAsync<Row>(new CommandDefinition(
                "SELECT AssetId, OutputPath, Recipe, ExportedAtMs FROM Exports WHERE AssetId IN @chunk ORDER BY ExportedAtMs;",
                new { chunk }, cancellationToken: ct)), ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                result[row.AssetId] = new ExportRecord(row.AssetId, row.OutputPath, row.Recipe, DateTimeOffset.FromUnixTimeMilliseconds(row.ExportedAtMs));
            }
        }

        return result;
    }

    private sealed record Row(long AssetId, string OutputPath, string Recipe, long ExportedAtMs);
}
