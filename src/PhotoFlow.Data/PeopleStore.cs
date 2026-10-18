// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Dapper;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Data;

internal sealed class PeopleStore(SqliteCatalog catalog) : IPeopleStore
{
    private const string FingerprintColumns =
        "f.Id AS FaceId, f.AssetId, CAST(strftime('%Y', a.CapturedAtMs / 1000, 'unixepoch') AS INTEGER) AS Year, f.EncryptedEmbedding, f.EmbeddingModel, f.PersonId, f.AssignmentOrigin";

    private static readonly string Manual = nameof(AssignmentOrigin.Manual);
    private static readonly string Automatic = nameof(AssignmentOrigin.Automatic);

    public async Task<IReadOnlyList<FaceFingerprint>> ListFingerprintsForAssetsAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var result = new List<FaceFingerprint>();
        foreach (var chunk in assetIds.Distinct().Chunk(500))
        {
            var rows = await catalog.RunAsync(c => c.QueryAsync<FingerprintRow>(new CommandDefinition(
                $"SELECT {FingerprintColumns} FROM Faces f JOIN Assets a ON a.Id = f.AssetId WHERE f.EncryptedEmbedding IS NOT NULL AND f.AssetId IN @chunk ORDER BY f.Id;",
                new { chunk }, cancellationToken: ct)), ct).ConfigureAwait(false);
            result.AddRange(rows.Select(Map));
        }

        return result;
    }

    public async Task<(int WithKeypoints, int WithFingerprint)> CountFingerprintsAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<(long WithKeypoints, long WithFingerprint)>(new CommandDefinition(
            "SELECT COUNT(Keypoints), COUNT(EncryptedEmbedding) FROM Faces;", cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => ((int)r.WithKeypoints, (int)r.WithFingerprint)).Single();

    public async Task<IReadOnlyList<FaceFingerprint>> ListAllFingerprintsAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<FingerprintRow>(new CommandDefinition(
            $"SELECT {FingerprintColumns} FROM Faces f JOIN Assets a ON a.Id = f.AssetId WHERE f.EncryptedEmbedding IS NOT NULL ORDER BY f.Id;",
            cancellationToken: ct)), ct).ConfigureAwait(false)).Select(Map).ToList();

    public async Task<IReadOnlyList<FaceFingerprint>> ListUnassignedFingerprintsAsync(int? year, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<FingerprintRow>(new CommandDefinition(
            $"""
            SELECT {FingerprintColumns} FROM Faces f JOIN Assets a ON a.Id = f.AssetId
            WHERE f.EncryptedEmbedding IS NOT NULL AND f.PersonId IS NULL AND (f.AssignmentOrigin IS NULL OR f.AssignmentOrigin <> @Manual)
              AND ((@year IS NULL AND a.CapturedAtMs IS NULL) OR CAST(strftime('%Y', a.CapturedAtMs / 1000, 'unixepoch') AS INTEGER) = @year)
            ORDER BY f.Id;
            """,
            new { year, Manual }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(Map).ToList();

    public async Task<IReadOnlyList<PersonPrototype>> ListPrototypesAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<(long Id, long PersonId, long FaceId, long? Year, byte[] EncryptedEmbedding, string ModelVersion)>(new CommandDefinition(
            "SELECT Id, PersonId, FaceId, Year, EncryptedEmbedding, ModelVersion FROM PersonPrototypes ORDER BY PersonId, Id;", cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new PersonPrototype(r.Id, r.PersonId, r.FaceId, (int?)r.Year, r.EncryptedEmbedding, r.ModelVersion)).ToList();

    public Task ReplacePrototypesAsync(long personId, IReadOnlyList<PersonPrototype> prototypes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prototypes);
        return catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM PersonPrototypes WHERE PersonId = @personId;", new { personId }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            foreach (var p in prototypes)
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO PersonPrototypes (PersonId, FaceId, Year, EncryptedEmbedding, ModelVersion) VALUES (@personId, @FaceId, @Year, @EncryptedEmbedding, @ModelVersion);",
                    new { personId, p.FaceId, p.Year, p.EncryptedEmbedding, p.ModelVersion }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            }

            transaction.Commit();
        }, ct);
    }

    public Task<long> CreatePersonAsync(string? name, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO Persons (Name, CreatedAtMs) VALUES (@name, @now); SELECT last_insert_rowid();",
            new { name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(), now = Db.Now() }, cancellationToken: ct)), ct);

    public Task AssignAutomaticAsync(IReadOnlyList<(long FaceId, long PersonId, double? Similarity)> faces, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(faces);
        return catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            foreach (var (faceId, personId, similarity) in faces)
            {
                await c.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE Faces SET PersonId = @personId, AssignmentOrigin = @Automatic, Similarity = @similarity, SuggestedPersonId = NULL
                    WHERE Id = @faceId AND (AssignmentOrigin IS NULL OR AssignmentOrigin <> @Manual);
                    """,
                    new { faceId, personId, similarity, Automatic, Manual }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            }

            transaction.Commit();
        }, ct);
    }

    public Task SuggestAsync(IReadOnlyList<(long FaceId, long? PersonId, double? Similarity)> faces, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(faces);
        return catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            foreach (var (faceId, personId, similarity) in faces)
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "UPDATE Faces SET SuggestedPersonId = @personId, Similarity = COALESCE(@similarity, Similarity) WHERE Id = @faceId AND PersonId IS NULL;",
                    new { faceId, personId, similarity }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            }

            transaction.Commit();
        }, ct);
    }

    public Task AssignManualAsync(IReadOnlyList<long> faceIds, long? personId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(faceIds);
        return catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            foreach (var faceId in faceIds)
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "UPDATE Faces SET PersonId = @personId, AssignmentOrigin = @Manual, SuggestedPersonId = NULL WHERE Id = @faceId;",
                    new { faceId, personId, Manual }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            }

            transaction.Commit();
        }, ct);
    }

    public async Task<IReadOnlyList<PersonSummary>> ListPersonsAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<SummaryRow>(new CommandDefinition(
            $"""
            SELECT p.Id, p.Name, p.Ignored, p.CoverFaceId,
                   COUNT(f.Id) AS FaceCount, COUNT(DISTINCT f.AssetId) AS PhotoCount,
                   MIN(CAST(strftime('%Y', a.CapturedAtMs / 1000, 'unixepoch') AS INTEGER)) AS FirstYear,
                   MAX(CAST(strftime('%Y', a.CapturedAtMs / 1000, 'unixepoch') AS INTEGER)) AS LastYear,
                   SUM(CASE WHEN f.AssignmentOrigin = @Manual THEN 1 ELSE 0 END) AS ManualFaces
            FROM Persons p LEFT JOIN Faces f ON f.PersonId = p.Id LEFT JOIN Assets a ON a.Id = f.AssetId
            GROUP BY p.Id ORDER BY FaceCount DESC, p.Id;
            """,
            new { Manual }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new PersonSummary(new Person(r.Id, r.Name, r.Ignored != 0, r.CoverFaceId), (int)r.FaceCount, (int)r.PhotoCount, (int?)r.FirstYear, (int?)r.LastYear, (int)(r.ManualFaces ?? 0)))
        .ToList();

    public async Task<Person?> GetPersonAsync(long personId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<(long Id, string? Name, long Ignored, long? CoverFaceId)>(new CommandDefinition(
            "SELECT Id, Name, Ignored, CoverFaceId FROM Persons WHERE Id = @personId;", new { personId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new Person(r.Id, r.Name, r.Ignored != 0, r.CoverFaceId)).FirstOrDefault();

    public async Task<IReadOnlyList<Face>> ListFacesOfPersonAsync(long personId, CancellationToken ct)
    {
        var ids = await catalog.RunAsync(c => c.QueryAsync<long>(new CommandDefinition(
            "SELECT DISTINCT AssetId FROM Faces WHERE PersonId = @personId;", new { personId }, cancellationToken: ct)), ct).ConfigureAwait(false);
        var faces = new List<Face>();
        foreach (var assetId in ids)
        {
            faces.AddRange((await catalog.Analysis.GetFacesAsync(assetId, ct).ConfigureAwait(false)).Where(f => f.PersonId == personId));
        }

        return faces;
    }

    public Task RenameAsync(long personId, string? name, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "UPDATE Persons SET Name = @name WHERE Id = @personId;", new { personId, name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() }, cancellationToken: ct)), ct);

    public Task SetIgnoredAsync(long personId, bool ignored, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "UPDATE Persons SET Ignored = @ignored WHERE Id = @personId;", new { personId, ignored }, cancellationToken: ct)), ct);

    public Task SetCoverAsync(long personId, long faceId, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "UPDATE Persons SET CoverFaceId = @faceId WHERE Id = @personId;", new { personId, faceId }, cancellationToken: ct)), ct);

    public Task MergeAsync(long targetId, IReadOnlyList<long> sourceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        var sources = sourceIds.Where(s => s != targetId).Distinct().ToList();
        return catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            var args = new { targetId, sources };
            await c.ExecuteAsync(new CommandDefinition("UPDATE Faces SET PersonId = @targetId WHERE PersonId IN @sources;", args, transaction, cancellationToken: ct)).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition("UPDATE Faces SET SuggestedPersonId = @targetId WHERE SuggestedPersonId IN @sources;", args, transaction, cancellationToken: ct)).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition("UPDATE PersonPrototypes SET PersonId = @targetId WHERE PersonId IN @sources;", args, transaction, cancellationToken: ct)).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM Persons WHERE Id IN @sources;", args, transaction, cancellationToken: ct)).ConfigureAwait(false);
            transaction.Commit();
        }, ct);
    }

    public Task DeletePersonAsync(long personId, CancellationToken ct) =>
        catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE Faces SET PersonId = NULL, AssignmentOrigin = NULL, Similarity = NULL, EncryptedEmbedding = NULL, EmbeddingModel = NULL WHERE PersonId = @personId;",
                new { personId }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition("UPDATE Faces SET SuggestedPersonId = NULL WHERE SuggestedPersonId = @personId;", new { personId }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM PersonPrototypes WHERE PersonId = @personId;", new { personId }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM Persons WHERE Id = @personId;", new { personId }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            transaction.Commit();
        }, ct);

    public async Task<IReadOnlyList<Face>> ListFacesWithKeypointsAsync(CancellationToken ct)
    {
        var ids = await catalog.RunAsync(c => c.QueryAsync<long>(new CommandDefinition(
            "SELECT DISTINCT AssetId FROM Faces WHERE Keypoints IS NOT NULL ORDER BY AssetId;", cancellationToken: ct)), ct).ConfigureAwait(false);
        var faces = new List<Face>();
        foreach (var assetId in ids)
        {
            faces.AddRange((await catalog.Analysis.GetFacesAsync(assetId, ct).ConfigureAwait(false)).Where(f => f.Keypoints is not null));
        }

        return faces;
    }

    public Task SetFingerprintAsync(long faceId, byte[]? encryptedEmbedding, string? embeddingModel, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "UPDATE Faces SET EncryptedEmbedding = @encryptedEmbedding, EmbeddingModel = @embeddingModel WHERE Id = @faceId;",
            new { faceId, encryptedEmbedding, embeddingModel }, cancellationToken: ct)), ct);

    public Task ClearFingerprintsAsync(CancellationToken ct) =>
        catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            await c.ExecuteAsync(new CommandDefinition("UPDATE Faces SET EncryptedEmbedding = NULL, EmbeddingModel = NULL;", transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM PersonPrototypes;", transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
            transaction.Commit();
        }, ct);

    public Task<PeopleReset> CountResetAsync(CancellationToken ct) =>
        catalog.RunAsync(async c => await CountResetAsync(c, null, ct).ConfigureAwait(false), ct);

    public Task<PeopleReset> ResetAsync(CancellationToken ct) =>
        catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            var reset = await CountResetAsync(c, transaction, ct).ConfigureAwait(false);
            var args = new { ManualVersion = FaceDefaults.ManualModelVersion };
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM Faces WHERE ModelVersion = @ManualVersion;", args, transaction, cancellationToken: ct)).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition(
                """
                UPDATE Faces SET PersonId = NULL, AssignmentOrigin = NULL, Similarity = NULL, SuggestedPersonId = NULL
                WHERE PersonId IS NOT NULL OR AssignmentOrigin IS NOT NULL OR SuggestedPersonId IS NOT NULL;
                """, transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM PersonPrototypes;", transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM Persons;", transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
            transaction.Commit();
            return reset;
        }, ct);

    private static async Task<PeopleReset> CountResetAsync(System.Data.IDbConnection c, System.Data.IDbTransaction? transaction, CancellationToken ct)
    {
        var args = new { Manual, ManualVersion = FaceDefaults.ManualModelVersion };
        var (persons, named) = (await c.QueryAsync<(long Persons, long Named)>(new CommandDefinition(
            "SELECT COUNT(*), COUNT(Name) FROM Persons;", transaction: transaction, cancellationToken: ct)).ConfigureAwait(false)).Single();
        var (manual, drawn) = (await c.QueryAsync<(long Manual, long Drawn)>(new CommandDefinition(
            """
            SELECT COALESCE(SUM(CASE WHEN AssignmentOrigin = @Manual AND ModelVersion <> @ManualVersion THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN ModelVersion = @ManualVersion THEN 1 ELSE 0 END), 0)
            FROM Faces;
            """, args, transaction, cancellationToken: ct)).ConfigureAwait(false)).Single();
        var names = await c.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT Name FROM Persons WHERE Name IS NOT NULL ORDER BY Name;", transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
        var assets = await c.QueryAsync<long>(new CommandDefinition(
            "SELECT DISTINCT AssetId FROM Faces WHERE PersonId IS NOT NULL OR ModelVersion = @ManualVersion ORDER BY AssetId;",
            args, transaction, cancellationToken: ct)).ConfigureAwait(false);
        return new PeopleReset((int)persons, (int)named, (int)manual, (int)drawn, [.. names], [.. assets]);
    }

    private static FaceFingerprint Map(FingerprintRow r) =>
        new(r.FaceId, r.AssetId, (int?)r.Year, r.EncryptedEmbedding, r.EmbeddingModel ?? string.Empty, r.PersonId, Db.ParseOrNull<AssignmentOrigin>(r.AssignmentOrigin));

    // Classes with setters, not records: SQLite reports a NULL expression column as a BLOB, which defeats Dapper's
    // constructor matching (the setters convert).
    private sealed class FingerprintRow
    {
        public long FaceId { get; set; }

        public long AssetId { get; set; }

        public long? Year { get; set; }

        public byte[] EncryptedEmbedding { get; set; } = [];

        public string? EmbeddingModel { get; set; }

        public long? PersonId { get; set; }

        public string? AssignmentOrigin { get; set; }
    }

    private sealed class SummaryRow
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public long Ignored { get; set; }

        public long? CoverFaceId { get; set; }

        public long FaceCount { get; set; }

        public long PhotoCount { get; set; }

        public long? FirstYear { get; set; }

        public long? LastYear { get; set; }

        public long? ManualFaces { get; set; }
    }
}
