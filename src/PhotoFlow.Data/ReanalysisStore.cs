// SPDX-License-Identifier: Apache-2.0

using Dapper;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Pipeline;

namespace PhotoFlow.Data;

internal sealed class ReanalysisStore(SqliteCatalog catalog) : IReanalysisStore
{
    public Task StageAsync(long jobId, long assetId, ReanalysisParts part, string payloadJson, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ReanalysisStaging (JobId, AssetId, Part, PayloadJson) VALUES (@jobId, @assetId, @part, @payloadJson)
            ON CONFLICT (JobId, AssetId, Part) DO UPDATE SET PayloadJson = excluded.PayloadJson;
            """,
            new { jobId, assetId, part = part.ToString(), payloadJson }, cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<(long AssetId, ReanalysisParts Part, string PayloadJson)>> ListAsync(long jobId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<(long AssetId, string Part, string PayloadJson)>(new CommandDefinition(
            "SELECT AssetId, Part, PayloadJson FROM ReanalysisStaging WHERE JobId = @jobId ORDER BY AssetId, Part;", new { jobId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => (r.AssetId, Db.Parse<ReanalysisParts>(r.Part), r.PayloadJson)).ToList();

    public Task<int> CountAsync(long jobId, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT AssetId) FROM ReanalysisStaging WHERE JobId = @jobId;", new { jobId }, cancellationToken: ct)), ct);

    public Task ClearAsync(long jobId, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition("DELETE FROM ReanalysisStaging WHERE JobId = @jobId;", new { jobId }, cancellationToken: ct)), ct);
}
