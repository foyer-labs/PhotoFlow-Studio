// SPDX-License-Identifier: Apache-2.0

using Dapper;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Data;

internal sealed class LabelStore(SqliteCatalog catalog) : ILabelStore
{
    public Task SetAsync(long assetId, PhotoLabel label, string sessionId, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Labels (AssetId, Label, SessionId, LabeledAtMs) VALUES (@assetId, @label, @sessionId, @now)
            ON CONFLICT (AssetId) DO UPDATE SET Label = excluded.Label, SessionId = excluded.SessionId, LabeledAtMs = excluded.LabeledAtMs;
            """,
            new { assetId, label = label.ToString(), sessionId, now = Db.Now() }, cancellationToken: ct)), ct);

    public Task RemoveAsync(long assetId, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition("DELETE FROM Labels WHERE AssetId = @assetId;", new { assetId }, cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<LabeledPhoto>> ListAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<(long AssetId, string Label, string SessionId, long LabeledAtMs)>(new CommandDefinition(
            "SELECT AssetId, Label, SessionId, LabeledAtMs FROM Labels ORDER BY LabeledAtMs;", cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new LabeledPhoto(r.AssetId, Db.Parse<PhotoLabel>(r.Label), r.SessionId, DateTimeOffset.FromUnixTimeMilliseconds(r.LabeledAtMs)))
        .ToList();
}
