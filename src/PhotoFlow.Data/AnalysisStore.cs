// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using Dapper;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Data;

internal sealed class AnalysisStore(SqliteCatalog catalog) : IAnalysisStore
{
    public Task SaveResultAsync(AnalyzerResultRecord result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        return catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM AnalyzerResults WHERE AssetId = @AssetId AND AnalyzerId = @AnalyzerId;",
                new { result.AssetId, result.AnalyzerId }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO AnalyzerResults (AssetId, AnalyzerId, ModelVersion, ProfileId, ScoresJson, CreatedAtMs)
                VALUES (@AssetId, @AnalyzerId, @ModelVersion, @ProfileId, @ScoresJson, @CreatedAtMs);
                """,
                new { result.AssetId, result.AnalyzerId, result.ModelVersion, result.ProfileId, ScoresJson = JsonSerializer.Serialize(result.Scores), CreatedAtMs = Db.Now() },
                transaction, cancellationToken: ct)).ConfigureAwait(false);
            transaction.Commit();
        }, ct);
    }

    public async Task<AnalyzerResultRecord?> GetResultAsync(long assetId, string analyzerId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<ResultRow>(new CommandDefinition(
            "SELECT Id, AssetId, AnalyzerId, ModelVersion, ProfileId, ScoresJson FROM AnalyzerResults WHERE AssetId = @assetId AND AnalyzerId = @analyzerId ORDER BY Id DESC LIMIT 1;",
            new { assetId, analyzerId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new AnalyzerResultRecord(r.Id, r.AssetId, r.AnalyzerId, r.ModelVersion, r.ProfileId,
            JsonSerializer.Deserialize<Dictionary<string, double>>(r.ScoresJson) ?? []))
        .FirstOrDefault();

    public async Task<IReadOnlyDictionary<long, AnalyzerResultRecord>> GetResultsAsync(IReadOnlyCollection<long> assetIds, string analyzerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var result = new Dictionary<long, AnalyzerResultRecord>(assetIds.Count);
        foreach (var chunk in assetIds.Distinct().Chunk(500))
        {
            var rows = await catalog.RunAsync(c => c.QueryAsync<ResultRow>(new CommandDefinition(
                "SELECT Id, AssetId, AnalyzerId, ModelVersion, ProfileId, ScoresJson FROM AnalyzerResults WHERE AnalyzerId = @analyzerId AND AssetId IN @chunk ORDER BY Id;",
                new { analyzerId, chunk }, cancellationToken: ct)), ct).ConfigureAwait(false);
            foreach (var r in rows)
            {
                result[r.AssetId] = new AnalyzerResultRecord(r.Id, r.AssetId, r.AnalyzerId, r.ModelVersion, r.ProfileId,
                    JsonSerializer.Deserialize<Dictionary<string, double>>(r.ScoresJson) ?? []);
            }
        }

        return result;
    }

    public Task SaveEmbeddingAsync(long assetId, string modelVersion, ReadOnlyMemory<float> vector, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Embeddings (AssetId, ModelVersion, Vector, CreatedAtMs) VALUES (@assetId, @modelVersion, @blob, @now)
            ON CONFLICT (AssetId, ModelVersion) DO UPDATE SET Vector = excluded.Vector, CreatedAtMs = excluded.CreatedAtMs;
            """,
            new { assetId, modelVersion, blob = MemoryMarshal.AsBytes(vector.Span).ToArray(), now = Db.Now() }, cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<long>> ListEmbeddedAssetsAsync(string modelVersion, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<long>(new CommandDefinition(
            "SELECT AssetId FROM Embeddings WHERE ModelVersion = @modelVersion ORDER BY AssetId;", new { modelVersion }, cancellationToken: ct)), ct).ConfigureAwait(false)).ToList();

    public async Task<IReadOnlyDictionary<long, float[]>> GetEmbeddingsAsync(IReadOnlyCollection<long> assetIds, string modelVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var result = new Dictionary<long, float[]>(assetIds.Count);
        foreach (var chunk in assetIds.Distinct().Chunk(500))
        {
            var rows = await catalog.RunAsync(c => c.QueryAsync<(long AssetId, byte[] Vector)>(new CommandDefinition(
                "SELECT AssetId, Vector FROM Embeddings WHERE ModelVersion = @modelVersion AND AssetId IN @chunk;",
                new { modelVersion, chunk }, cancellationToken: ct)), ct).ConfigureAwait(false);
            foreach (var (assetId, vector) in rows)
            {
                result[assetId] = MemoryMarshal.Cast<byte, float>(vector).ToArray();
            }
        }

        return result;
    }

    public Task ReplaceAutomaticFacesAsync(long assetId, IReadOnlyList<Face> faces, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(faces);
        return catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM Faces WHERE AssetId = @assetId AND (AssignmentOrigin IS NULL OR AssignmentOrigin <> @manual);",
                new { assetId, manual = nameof(AssignmentOrigin.Manual) }, transaction, cancellationToken: ct)).ConfigureAwait(false);

            // Faces decided by the user stay as they are: the same face found again is not added twice.
            var kept = (await c.QueryAsync<(double X1, double Y1, double X2, double Y2)>(new CommandDefinition(
                "SELECT X1, Y1, X2, Y2 FROM Faces WHERE AssetId = @assetId;", new { assetId }, transaction, cancellationToken: ct)).ConfigureAwait(false))
                .Select(r => new BoundingBox((float)r.X1, (float)r.Y1, (float)r.X2, (float)r.Y2)).ToList();
            foreach (var face in faces.Where(f => !kept.Any(k => k.IntersectionOverUnion(f.Box) >= 0.5f)))
            {
                await c.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO Faces (AssetId, X1, Y1, X2, Y2, Landmarks, Confidence, LeftEye, RightEye, EncryptedEmbedding, PersonId, AssignmentOrigin, Similarity, ModelVersion, Keypoints, EmbeddingModel)
                    VALUES (@AssetId, @X1, @Y1, @X2, @Y2, @Landmarks, @Confidence, @LeftEye, @RightEye, @EncryptedEmbedding, @PersonId, @AssignmentOrigin, @Similarity, @ModelVersion, @Keypoints, @EmbeddingModel);
                    """,
                    new
                    {
                        AssetId = assetId,
                        face.Box.X1,
                        face.Box.Y1,
                        face.Box.X2,
                        face.Box.Y2,
                        Landmarks = face.Landmarks is null ? null : MemoryMarshal.AsBytes(face.Landmarks.ToArray().AsSpan()).ToArray(),
                        face.Confidence,
                        LeftEye = face.LeftEye.ToString(),
                        RightEye = face.RightEye.ToString(),
                        face.EncryptedEmbedding,
                        face.PersonId,
                        AssignmentOrigin = Db.Name(face.AssignmentOrigin),
                        face.Similarity,
                        face.ModelVersion,
                        Keypoints = face.Keypoints is null ? null : MemoryMarshal.AsBytes(face.Keypoints.ToArray().AsSpan()).ToArray(),
                        face.EmbeddingModel,
                    },
                    transaction, cancellationToken: ct)).ConfigureAwait(false);
            }

            transaction.Commit();
        }, ct);
    }

    public Task<long> AddManualFaceAsync(long assetId, BoundingBox box, long? personId, CancellationToken ct)
    {
        if (box.X1 < 0 || box.Y1 < 0 || box.X2 > 1 || box.Y2 > 1 || box.X2 - box.X1 < FaceDefaults.MinManualSide || box.Y2 - box.Y1 < FaceDefaults.MinManualSide)
        {
            throw new ArgumentOutOfRangeException(nameof(box), box, "The face must be inside the photo and not empty.");
        }

        return catalog.RunAsync(c => c.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO Faces (AssetId, X1, Y1, X2, Y2, Confidence, LeftEye, RightEye, PersonId, AssignmentOrigin, ModelVersion)
            VALUES (@assetId, @X1, @Y1, @X2, @Y2, 1, @unknown, @unknown, @personId, @manual, @version)
            RETURNING Id;
            """,
            new
            {
                assetId,
                box.X1,
                box.Y1,
                box.X2,
                box.Y2,
                unknown = nameof(EyeState.Unknown),
                personId,
                manual = nameof(AssignmentOrigin.Manual),
                version = FaceDefaults.ManualModelVersion,
            }, cancellationToken: ct)), ct);
    }

    public Task<IReadOnlyList<long>> ReplaceFacesAsync(long assetId, IReadOnlyList<Face> faces, IReadOnlyCollection<long> keep,
        IReadOnlyDictionary<int, long> replaces, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(replaces);
        return catalog.RunAsync<IReadOnlyList<long>>(async c =>
        {
            using var transaction = c.BeginTransaction();
            var ids = new List<long>(faces.Count);
            foreach (var face in faces)
            {
                ids.Add(await c.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    INSERT INTO Faces (AssetId, X1, Y1, X2, Y2, Landmarks, Confidence, LeftEye, RightEye, EncryptedEmbedding, PersonId, AssignmentOrigin, Similarity, ModelVersion, Keypoints, EmbeddingModel)
                    VALUES (@AssetId, @X1, @Y1, @X2, @Y2, @Landmarks, @Confidence, @LeftEye, @RightEye, @EncryptedEmbedding, @PersonId, @AssignmentOrigin, @Similarity, @ModelVersion, @Keypoints, @EmbeddingModel)
                    RETURNING Id;
                    """,
                    new
                    {
                        AssetId = assetId,
                        face.Box.X1,
                        face.Box.Y1,
                        face.Box.X2,
                        face.Box.Y2,
                        Landmarks = face.Landmarks is null ? null : MemoryMarshal.AsBytes(face.Landmarks.ToArray().AsSpan()).ToArray(),
                        face.Confidence,
                        LeftEye = face.LeftEye.ToString(),
                        RightEye = face.RightEye.ToString(),
                        face.EncryptedEmbedding,
                        face.PersonId,
                        AssignmentOrigin = Db.Name(face.AssignmentOrigin),
                        face.Similarity,
                        face.ModelVersion,
                        Keypoints = face.Keypoints is null ? null : MemoryMarshal.AsBytes(face.Keypoints.ToArray().AsSpan()).ToArray(),
                        face.EmbeddingModel,
                    },
                    transaction, cancellationToken: ct)).ConfigureAwait(false));
            }

            foreach (var (index, oldId) in replaces)
            {
                await c.ExecuteAsync(new CommandDefinition("UPDATE Persons SET CoverFaceId = @newId WHERE CoverFaceId = @oldId;",
                    new { newId = ids[index], oldId }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            }

            var remaining = ids.Concat(keep).ToList();
            var removed = (await c.QueryAsync<long>(new CommandDefinition("SELECT Id FROM Faces WHERE AssetId = @assetId AND Id NOT IN @remaining;",
                new { assetId, remaining }, transaction, cancellationToken: ct)).ConfigureAwait(false)).ToList();
            foreach (var chunk in removed.Chunk(500))
            {
                await c.ExecuteAsync(new CommandDefinition("DELETE FROM PersonPrototypes WHERE FaceId IN @chunk;", new { chunk }, transaction, cancellationToken: ct)).ConfigureAwait(false);
                await c.ExecuteAsync(new CommandDefinition("UPDATE Persons SET CoverFaceId = NULL WHERE CoverFaceId IN @chunk;", new { chunk }, transaction, cancellationToken: ct)).ConfigureAwait(false);
                await c.ExecuteAsync(new CommandDefinition("DELETE FROM Faces WHERE Id IN @chunk;", new { chunk }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            }

            transaction.Commit();
            return ids;
        }, ct);
    }

    public async Task<IReadOnlyList<Face>> GetFacesAsync(long assetId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<FaceRow>(new CommandDefinition(
            """
            SELECT Id, AssetId, X1, Y1, X2, Y2, Landmarks, Confidence, LeftEye, RightEye, EncryptedEmbedding, PersonId, AssignmentOrigin, Similarity, ModelVersion, Keypoints, EmbeddingModel, SuggestedPersonId
            FROM Faces WHERE AssetId = @assetId ORDER BY Id;
            """,
            new { assetId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new Face(
            r.Id, r.AssetId, new BoundingBox((float)r.X1, (float)r.Y1, (float)r.X2, (float)r.Y2),
            r.Landmarks is null ? null : MemoryMarshal.Cast<byte, Point2>(r.Landmarks).ToArray(),
            r.Confidence, Db.Parse<EyeState>(r.LeftEye), Db.Parse<EyeState>(r.RightEye), r.EncryptedEmbedding, r.PersonId,
            Db.ParseOrNull<AssignmentOrigin>(r.AssignmentOrigin), r.Similarity, r.ModelVersion,
            r.Keypoints is null ? null : MemoryMarshal.Cast<byte, Point2>(r.Keypoints).ToArray(), r.EmbeddingModel, r.SuggestedPersonId))
        .ToList();

    private sealed class ResultRow
    {
        public long Id { get; set; }

        public long AssetId { get; set; }

        public string AnalyzerId { get; set; } = "";

        public string ModelVersion { get; set; } = "";

        public string ProfileId { get; set; } = "";

        public string ScoresJson { get; set; } = "";
    }

    private sealed class FaceRow
    {
        public long Id { get; set; }

        public long AssetId { get; set; }

        public double X1 { get; set; }

        public double Y1 { get; set; }

        public double X2 { get; set; }

        public double Y2 { get; set; }

        public byte[]? Landmarks { get; set; }

        public double Confidence { get; set; }

        public string LeftEye { get; set; } = "";

        public string RightEye { get; set; } = "";

        public byte[]? EncryptedEmbedding { get; set; }

        public long? PersonId { get; set; }

        public string? AssignmentOrigin { get; set; }

        public double? Similarity { get; set; }

        public string ModelVersion { get; set; } = "";

        public byte[]? Keypoints { get; set; }

        public string? EmbeddingModel { get; set; }

        public long? SuggestedPersonId { get; set; }
    }
}
