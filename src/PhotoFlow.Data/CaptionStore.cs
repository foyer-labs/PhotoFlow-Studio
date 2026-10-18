// SPDX-License-Identifier: Apache-2.0

using Dapper;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Data;

internal sealed class CaptionStore(SqliteCatalog catalog) : ICaptionStore
{
    private const string Columns = "AssetId, Language, Text, ModelVersion, Origin, UpdatedAtMs";

    // Italian first (phase 5b, U5), then the user's, then the most recent.
    private const string Preferred = "Language = 'it' DESC, Origin = 'Manual' DESC, UpdatedAtMs DESC";

    public async Task<bool> SetAutomaticAsync(long assetId, string language, string text, string modelVersion, CancellationToken ct) =>
        await catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Captions (AssetId, Language, Text, ModelVersion, Origin, UpdatedAtMs) VALUES (@assetId, @language, @text, @modelVersion, 'Automatic', @now)
            ON CONFLICT (AssetId, Language) DO UPDATE SET Text = excluded.Text, ModelVersion = excluded.ModelVersion, UpdatedAtMs = excluded.UpdatedAtMs
            WHERE Captions.Origin = 'Automatic';
            """,
            new { assetId, language, text, modelVersion, now = Db.Now() }, cancellationToken: ct)), ct).ConfigureAwait(false) > 0;

    public Task SetManualAsync(long assetId, string language, string text, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Captions (AssetId, Language, Text, ModelVersion, Origin, UpdatedAtMs) VALUES (@assetId, @language, @text, NULL, 'Manual', @now)
            ON CONFLICT (AssetId, Language) DO UPDATE SET Text = excluded.Text, ModelVersion = NULL, Origin = 'Manual', UpdatedAtMs = excluded.UpdatedAtMs;
            """,
            new { assetId, language, text, now = Db.Now() }, cancellationToken: ct)), ct);

    public async Task<Caption?> GetAsync(long assetId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM Captions WHERE AssetId = @assetId ORDER BY {Preferred} LIMIT 1;",
            new { assetId }, cancellationToken: ct)), ct).ConfigureAwait(false)).Select(ToCaption).FirstOrDefault();

    public async Task<IReadOnlyList<Caption>> GetAllAsync(long assetId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM Captions WHERE AssetId = @assetId ORDER BY {Preferred};",
            new { assetId }, cancellationToken: ct)), ct).ConfigureAwait(false)).Select(ToCaption).ToList();

    public async Task<IReadOnlyDictionary<long, Caption>> GetForAssetsAsync(IReadOnlyList<long> assetIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var result = new Dictionary<long, Caption>();
        foreach (var chunk in assetIds.Distinct().Chunk(500))
        {
            var rows = await catalog.RunAsync(c => c.QueryAsync<Row>(new CommandDefinition(
                $"SELECT {Columns} FROM Captions WHERE AssetId IN @chunk ORDER BY Language = 'it', Origin = 'Manual', UpdatedAtMs;",
                new { chunk }, cancellationToken: ct)), ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                result[row.AssetId] = ToCaption(row); // the preferred one comes last
            }
        }

        return result;
    }

    private static Caption ToCaption(Row r) =>
        new(r.AssetId, r.Language, r.Text, r.ModelVersion, Db.Parse<CaptionOrigin>(r.Origin), DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAtMs));

    private sealed class Row
    {
        public long AssetId { get; set; }

        public string Language { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public string? ModelVersion { get; set; }

        public string Origin { get; set; } = string.Empty;

        public long UpdatedAtMs { get; set; }
    }
}
