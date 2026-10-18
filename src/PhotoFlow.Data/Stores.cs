// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Dapper;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;

namespace PhotoFlow.Data;

internal sealed class LibraryStore(SqliteCatalog catalog) : ILibraryStore
{
    private const string Columns = "Id, Name, RootPath, XmpPolicy, IsReadOnly, Origin, LastScanMs";

    public Task<long> AddAsync(Library library, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO Libraries (Name, RootPath, XmpPolicy, IsReadOnly, Origin, LastScanMs) VALUES (@Name, @RootPath, @XmpPolicy, @IsReadOnly, @Origin, @LastScanMs) RETURNING Id;",
            new { library.Name, library.RootPath, XmpPolicy = library.XmpPolicy.ToString(), library.IsReadOnly, Origin = library.Origin.ToString(), LastScanMs = Db.Ms(library.LastScanAt) },
            cancellationToken: ct)), ct);

    public async Task<Library?> GetAsync(long id, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<Row>(new CommandDefinition($"SELECT {Columns} FROM Libraries WHERE Id = @id;", new { id }, cancellationToken: ct)), ct).ConfigureAwait(false))
            .Select(Map).FirstOrDefault();

    public async Task<IReadOnlyList<Library>> ListAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<Row>(new CommandDefinition($"SELECT {Columns} FROM Libraries ORDER BY Name;", cancellationToken: ct)), ct).ConfigureAwait(false))
            .Select(Map).ToList();

    public async Task<Library?> FindByRootAsync(string rootPath, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM Libraries WHERE RootPath = @rootPath COLLATE NOCASE ORDER BY Id LIMIT 1;", new { rootPath }, cancellationToken: ct)), ct).ConfigureAwait(false))
            .Select(Map).FirstOrDefault();

    public async Task<IReadOnlyDictionary<string, int>> CountFormatsAsync(long libraryId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<(string Format, int Count)>(new CommandDefinition(
            "SELECT a.Format, COUNT(DISTINCT a.Id) FROM Assets a JOIN AssetLocations l ON l.AssetId = a.Id WHERE l.LibraryId = @libraryId GROUP BY a.Format;",
            new { libraryId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .ToDictionary(r => r.Format, r => r.Count, StringComparer.OrdinalIgnoreCase);

    public Task UpdatePolicyAsync(long libraryId, WritePolicy policy, bool isReadOnly, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "UPDATE Libraries SET XmpPolicy = @policy, IsReadOnly = @isReadOnly WHERE Id = @libraryId;",
            new { libraryId, policy = policy.ToString(), isReadOnly }, cancellationToken: ct)), ct);

    public Task UpdateRootAsync(long libraryId, string rootPath, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "UPDATE Libraries SET RootPath = @rootPath WHERE Id = @libraryId;", new { libraryId, rootPath }, cancellationToken: ct)), ct);

    public Task<int> CountAssetsAsync(long libraryId, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT AssetId) FROM AssetLocations WHERE LibraryId = @libraryId;", new { libraryId }, cancellationToken: ct)), ct);

    private static Library Map(Row r) =>
        new(r.Id, r.Name, r.RootPath, Db.Parse<WritePolicy>(r.XmpPolicy), r.IsReadOnly, Db.Parse<LibraryOrigin>(r.Origin), Db.Instant(r.LastScanMs));

    private sealed class Row
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";

        public string RootPath { get; set; } = "";

        public string XmpPolicy { get; set; } = "";

        public bool IsReadOnly { get; set; }

        public string Origin { get; set; } = "";

        public long? LastScanMs { get; set; }
    }
}

internal sealed class AssetStore(SqliteCatalog catalog) : IAssetStore
{
    private const string Columns =
        "Id, QuickHash, FullHash, Format, Handler, Width, Height, CapturedAtMs, CapturedOffsetMin, CaptureDateSource, CameraModel, Lens, Iso, " +
        "ExposureTimeSeconds, Aperture, FocalLengthMm, ExposureCompensation, AfPoint, AfArea, State";

    public Task<long> AddAsync(Asset asset, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO Assets (QuickHash, FullHash, Format, Handler, Width, Height, CapturedAtMs, CapturedOffsetMin, CaptureDateSource, CameraModel, Lens, Iso,
                                ExposureTimeSeconds, Aperture, FocalLengthMm, ExposureCompensation, AfPoint, AfArea, State, CreatedAtMs)
            VALUES (@QuickHash, @FullHash, @Format, @Handler, @Width, @Height, @CapturedAtMs, @CapturedOffsetMin, @CaptureDateSource, @CameraModel, @Lens, @Iso,
                    @ExposureTimeSeconds, @Aperture, @FocalLengthMm, @ExposureCompensation, @AfPoint, @AfArea, @State, @CreatedAtMs)
            RETURNING Id;
            """,
            new
            {
                asset.QuickHash,
                asset.FullHash,
                asset.Format,
                asset.Handler,
                asset.Width,
                asset.Height,
                CapturedAtMs = Db.Ms(asset.CapturedAt),
                CapturedOffsetMin = asset.CapturedAt is { } at ? (long?)at.Offset.TotalMinutes : null,
                CaptureDateSource = asset.CaptureDateSource.ToString(),
                asset.Exif?.CameraModel,
                asset.Exif?.Lens,
                asset.Exif?.Iso,
                asset.Exif?.ExposureTimeSeconds,
                asset.Exif?.Aperture,
                asset.Exif?.FocalLengthMm,
                ExposureCompensation = asset.Exif?.ExposureCompensationEv,
                asset.Exif?.AfPoint,
                AfArea = Db.Box(asset.Exif?.AfArea),
                State = asset.State.ToString(),
                CreatedAtMs = Db.Now(),
            },
            cancellationToken: ct)), ct);

    public Task<Asset?> GetAsync(long id, CancellationToken ct) => QuerySingleAsync("Id = @id", new { id }, ct);

    public Task<Asset?> FindByIdentityAsync(string quickHash, DateTimeOffset? capturedAt, CancellationToken ct) =>
        capturedAt is null
            ? QuerySingleAsync("QuickHash = @quickHash AND CapturedAtMs IS NULL", new { quickHash }, ct)
            : QuerySingleAsync("QuickHash = @quickHash AND CapturedAtMs = @ms", new { quickHash, ms = Db.Ms(capturedAt) }, ct);

    public Task<Asset?> FindByFullHashAsync(string fullHash, CancellationToken ct) => QuerySingleAsync("FullHash = @fullHash", new { fullHash }, ct);

    public async Task<IReadOnlyList<(long AssetId, string RelativePath)>> ListWithoutFullHashAsync(long libraryId, int limit, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<(long, string)>(new CommandDefinition(
            """
            SELECT a.Id, MIN(l.RelativePath) FROM Assets a JOIN AssetLocations l ON l.AssetId = a.Id
            WHERE l.LibraryId = @libraryId AND a.FullHash IS NULL
            GROUP BY a.Id ORDER BY a.Id LIMIT @limit;
            """,
            new { libraryId, limit }, cancellationToken: ct)), ct).ConfigureAwait(false)).ToList();

    public async Task<IReadOnlyList<(long AssetId, string RelativePath)>> ListInLibraryAsync(long libraryId, string? folder, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<(long, string)>(new CommandDefinition(
            """
            SELECT a.Id, MIN(l.RelativePath) FROM Assets a JOIN AssetLocations l ON l.AssetId = a.Id
            WHERE l.LibraryId = @libraryId AND a.State = 'Active' AND (@prefix IS NULL OR l.RelativePath LIKE @prefix ESCAPE '\')
            GROUP BY a.Id ORDER BY MIN(l.RelativePath);
            """,
            new { libraryId, prefix = folder is null ? null : Escape(folder.TrimEnd('/', '\\')) + "%" }, cancellationToken: ct)), ct).ConfigureAwait(false)).ToList();

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    public Task SetFullHashAsync(long assetId, string fullHash, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "UPDATE Assets SET FullHash = @fullHash WHERE Id = @assetId AND FullHash IS NULL;", new { assetId, fullHash }, cancellationToken: ct)), ct);

    public Task MoveLocationAsync(long libraryId, string fromRelativePath, string toRelativePath, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "UPDATE AssetLocations SET RelativePath = @toRelativePath, LastSeenMs = @now WHERE LibraryId = @libraryId AND RelativePath = @fromRelativePath;",
            new { libraryId, fromRelativePath, toRelativePath, now = Db.Now() }, cancellationToken: ct)), ct);

    public Task AddLocationAsync(AssetLocation location, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO AssetLocations (AssetId, LibraryId, RelativePath, LastSeenMs) VALUES (@AssetId, @LibraryId, @RelativePath, @LastSeenMs)
            ON CONFLICT (LibraryId, RelativePath) DO UPDATE SET AssetId = excluded.AssetId, LastSeenMs = excluded.LastSeenMs;
            """,
            new { location.AssetId, location.LibraryId, location.RelativePath, LastSeenMs = Db.Ms(location.LastSeenAt) },
            cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<AssetLocation>> GetLocationsAsync(long assetId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<LocationRow>(new CommandDefinition(
            "SELECT Id, AssetId, LibraryId, RelativePath, LastSeenMs FROM AssetLocations WHERE AssetId = @assetId ORDER BY Id;", new { assetId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new AssetLocation(r.Id, r.AssetId, r.LibraryId, r.RelativePath, Db.Instant(r.LastSeenMs)!.Value))
        .ToList();

    public async Task<IReadOnlyDictionary<long, Asset>> GetManyAsync(IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var result = new Dictionary<long, Asset>(ids.Count);
        foreach (var chunk in ids.Distinct().Chunk(500))
        {
            foreach (var id in chunk)
            {
                if (await GetAsync(id, ct).ConfigureAwait(false) is { } asset)
                {
                    result[id] = asset;
                }
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> ListCamerasAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT CameraModel FROM Assets WHERE CameraModel IS NOT NULL ORDER BY CameraModel;", cancellationToken: ct)), ct).ConfigureAwait(false)).ToList();

    public async Task<long?> GetTwinAsync(long assetId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<long>(new CommandDefinition(
            "SELECT CASE WHEN RawAssetId = @assetId THEN JpegAssetId ELSE RawAssetId END FROM AssetPairs WHERE RawAssetId = @assetId OR JpegAssetId = @assetId LIMIT 1;",
            new { assetId }, cancellationToken: ct)), ct).ConfigureAwait(false)).Cast<long?>().FirstOrDefault();

    public Task AddPairAsync(long rawAssetId, long jpegAssetId, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "INSERT OR IGNORE INTO AssetPairs (RawAssetId, JpegAssetId) VALUES (@rawAssetId, @jpegAssetId);", new { rawAssetId, jpegAssetId }, cancellationToken: ct)), ct);

    private async Task<Asset?> QuerySingleAsync(string where, object parameters, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<Row>(new CommandDefinition($"SELECT {Columns} FROM Assets WHERE {where} ORDER BY Id LIMIT 1;", parameters, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(Map).FirstOrDefault();

    private static Asset Map(Row r)
    {
        var hasExif = r.CameraModel is not null || r.Lens is not null || r.Iso is not null || r.ExposureTimeSeconds is not null || r.Aperture is not null
                      || r.FocalLengthMm is not null || r.ExposureCompensation is not null || r.AfPoint is not null || r.AfArea is not null;
        var exif = hasExif
            ? new ExifSummary(r.CameraModel, r.Lens, r.Iso, r.ExposureTimeSeconds, r.Aperture, r.FocalLengthMm, r.ExposureCompensation, r.AfPoint, Db.Box(r.AfArea))
            : null;
        return new Asset(r.Id, r.QuickHash, r.FullHash, r.Format, r.Handler, r.Width, r.Height, Db.Instant(r.CapturedAtMs, r.CapturedOffsetMin),
            Db.Parse<DateSource>(r.CaptureDateSource), exif, Db.Parse<AssetState>(r.State));
    }

    private sealed class Row
    {
        public long Id { get; set; }

        public string QuickHash { get; set; } = "";

        public string? FullHash { get; set; }

        public string Format { get; set; } = "";

        public string Handler { get; set; } = "";

        public int? Width { get; set; }

        public int? Height { get; set; }

        public long? CapturedAtMs { get; set; }

        public long? CapturedOffsetMin { get; set; }

        public string CaptureDateSource { get; set; } = "";

        public string? CameraModel { get; set; }

        public string? Lens { get; set; }

        public int? Iso { get; set; }

        public double? ExposureTimeSeconds { get; set; }

        public double? Aperture { get; set; }

        public double? FocalLengthMm { get; set; }

        public double? ExposureCompensation { get; set; }

        public string? AfPoint { get; set; }

        public string? AfArea { get; set; }

        public string State { get; set; } = "";
    }

    private sealed class LocationRow
    {
        public long Id { get; set; }

        public long AssetId { get; set; }

        public long LibraryId { get; set; }

        public string RelativePath { get; set; } = "";

        public long LastSeenMs { get; set; }
    }
}

internal sealed class JobStore(SqliteCatalog catalog) : IJobStore
{
    private const string JobColumns =
        "Id, Type, Source, EventName, EventDate, ProfileId, AutomaticProfile, FallbackProfileId, State, Priority, SettingsJson, CreatedAtMs";

    private const string ItemColumns = "Id, JobId, AssetId, SourcePath, TargetRelativePath, ContentHash, LastCompletedStage, State, Error";

    public Task<long> CreateAsync(Job job, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO Jobs (Type, Source, EventName, EventDate, ProfileId, AutomaticProfile, FallbackProfileId, State, Priority, SettingsJson, CreatedAtMs, UpdatedAtMs)
            VALUES (@Type, @Source, @EventName, @EventDate, @ProfileId, @AutomaticProfile, @FallbackProfileId, @State, @Priority, @SettingsJson, @CreatedAtMs, @CreatedAtMs)
            RETURNING Id;
            """,
            new
            {
                Type = job.Type.ToString(),
                job.Source,
                job.EventName,
                EventDate = Db.Date(job.EventDate),
                job.ProfileId,
                job.AutomaticProfile,
                job.FallbackProfileId,
                State = job.State.ToString(),
                Priority = job.Priority.ToString(),
                job.SettingsSnapshotJson,
                SettingsJson = job.SettingsSnapshotJson,
                CreatedAtMs = Db.Ms(job.CreatedAt),
            },
            cancellationToken: ct)), ct);

    public async Task<Job?> GetAsync(long id, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<JobRow>(new CommandDefinition($"SELECT {JobColumns} FROM Jobs WHERE Id = @id;", new { id }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(Map).FirstOrDefault();

    public Task SetStateAsync(long jobId, JobState state, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "UPDATE Jobs SET State = @state, UpdatedAtMs = @now WHERE Id = @jobId;", new { jobId, state = state.ToString(), now = Db.Now() }, cancellationToken: ct)), ct);

    public Task SetAutomaticProfileAsync(long jobId, bool automatic, string? fallbackProfileId, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "UPDATE Jobs SET AutomaticProfile = @automatic, FallbackProfileId = @fallbackProfileId, UpdatedAtMs = @now WHERE Id = @jobId;",
            new { jobId, automatic, fallbackProfileId, now = Db.Now() }, cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<Job>> ListUnfinishedAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<JobRow>(new CommandDefinition(
            $"SELECT {JobColumns} FROM Jobs WHERE State IN ('Pending', 'Running', 'Paused') ORDER BY Id;", cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(Map).ToList();

    public async Task<IReadOnlyList<Job>> ListRecentAsync(int limit, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<JobRow>(new CommandDefinition(
            $"SELECT {JobColumns} FROM Jobs ORDER BY CreatedAtMs DESC, Id DESC LIMIT @limit;", new { limit }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(Map).ToList();

    public Task AddItemsAsync(long jobId, IReadOnlyList<JobItem> items, CancellationToken ct) =>
        catalog.RunAsync(async c =>
        {
            await using var transaction = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition(
                """
                INSERT OR IGNORE INTO JobItems (JobId, AssetId, SourcePath, TargetRelativePath, ContentHash, LastCompletedStage, State, Error, UpdatedAtMs)
                VALUES (@JobId, @AssetId, @SourcePath, @TargetRelativePath, @ContentHash, @LastCompletedStage, @State, @Error, @UpdatedAtMs);
                """,
                items.Select(i => new
                {
                    JobId = jobId,
                    i.AssetId,
                    i.SourcePath,
                    i.TargetRelativePath,
                    i.ContentHash,
                    LastCompletedStage = i.LastCompletedStage.ToString(),
                    State = i.State.ToString(),
                    i.Error,
                    UpdatedAtMs = Db.Now(),
                }),
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }, ct);

    public Task UpdateItemAsync(JobItem item, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE JobItems SET AssetId = @AssetId, TargetRelativePath = @TargetRelativePath, ContentHash = @ContentHash,
                   LastCompletedStage = @LastCompletedStage, State = @State, Error = @Error, UpdatedAtMs = @UpdatedAtMs
            WHERE Id = @Id;
            """,
            new
            {
                item.Id,
                item.AssetId,
                item.TargetRelativePath,
                item.ContentHash,
                LastCompletedStage = item.LastCompletedStage.ToString(),
                State = item.State.ToString(),
                item.Error,
                UpdatedAtMs = Db.Now(),
            },
            cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<JobItem>> GetItemsAsync(long jobId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<ItemRow>(new CommandDefinition(
            $"SELECT {ItemColumns} FROM JobItems WHERE JobId = @jobId ORDER BY Id;", new { jobId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new JobItem(r.Id, r.JobId, r.AssetId, r.SourcePath, r.TargetRelativePath, r.ContentHash,
            Db.Parse<PipelineStage>(r.LastCompletedStage), Db.Parse<JobItemState>(r.State), r.Error))
        .ToList();

    private static Job Map(JobRow r) =>
        new(r.Id, Db.Parse<JobType>(r.Type), r.Source, r.EventName, Db.Date(r.EventDate), r.ProfileId, r.AutomaticProfile, r.FallbackProfileId,
            Db.Parse<JobState>(r.State), Db.Parse<JobPriority>(r.Priority), r.SettingsJson, Db.Instant(r.CreatedAtMs)!.Value);

    private sealed class JobRow
    {
        public long Id { get; set; }

        public string Type { get; set; } = "";

        public string? Source { get; set; }

        public string? EventName { get; set; }

        public string? EventDate { get; set; }

        public string? ProfileId { get; set; }

        public bool AutomaticProfile { get; set; }

        public string? FallbackProfileId { get; set; }

        public string State { get; set; } = "";

        public string Priority { get; set; } = "";

        public string SettingsJson { get; set; } = "";

        public long CreatedAtMs { get; set; }
    }

    private sealed class ItemRow
    {
        public long Id { get; set; }

        public long JobId { get; set; }

        public long? AssetId { get; set; }

        public string SourcePath { get; set; } = "";

        public string? TargetRelativePath { get; set; }

        public string? ContentHash { get; set; }

        public string LastCompletedStage { get; set; } = "";

        public string State { get; set; } = "";

        public string? Error { get; set; }
    }
}

/// <summary>Automatic and user columns are written by separate statements: the automatic one never touches user fields (I5).</summary>
internal sealed class DecisionStore(SqliteCatalog catalog) : IDecisionStore
{
    public Task AddCorrectionAsync(Correction correction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(correction);
        return catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Corrections (AssetId, AutomaticOutcome, UserOutcome, ProfileId, SignalsJson, CorrectedAtMs)
            VALUES (@AssetId, @AutomaticOutcome, @UserOutcome, @ProfileId, @SignalsJson, @CorrectedAtMs);
            """,
            new
            {
                correction.AssetId,
                AutomaticOutcome = Db.Name(correction.AutomaticOutcome),
                UserOutcome = correction.UserOutcome.ToString(),
                correction.ProfileId,
                SignalsJson = System.Text.Json.JsonSerializer.Serialize(correction.Signals),
                CorrectedAtMs = Db.Ms(correction.CorrectedAt),
            },
            cancellationToken: ct)), ct);
    }

    public async Task<IReadOnlyList<Correction>> ListCorrectionsAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<(long Id, long AssetId, string? AutomaticOutcome, string UserOutcome, string ProfileId, string SignalsJson, long CorrectedAtMs)>(
            new CommandDefinition("SELECT Id, AssetId, AutomaticOutcome, UserOutcome, ProfileId, SignalsJson, CorrectedAtMs FROM Corrections ORDER BY Id;", cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new Correction(r.Id, r.AssetId, Db.ParseOrNull<CullingOutcome>(r.AutomaticOutcome), Db.Parse<CullingOutcome>(r.UserOutcome), r.ProfileId,
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(r.SignalsJson) ?? [], DateTimeOffset.FromUnixTimeMilliseconds(r.CorrectedAtMs)))
        .ToList();

    public async Task<Decision?> GetAsync(long assetId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<Row>(new CommandDefinition(
            "SELECT Id, AssetId, AutomaticOutcome, ReasonsJson, ModelVersion, UserOutcome, Rating, Label, UserUpdatedAtMs FROM Decisions WHERE AssetId = @assetId;",
            new { assetId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new Decision(r.Id, r.AssetId, Db.ParseOrNull<CullingOutcome>(r.AutomaticOutcome),
            JsonSerializer.Deserialize<string[]>(r.ReasonsJson) ?? [], r.ModelVersion, Db.ParseOrNull<CullingOutcome>(r.UserOutcome), r.Rating, r.Label)
        {
            UserUpdatedAt = Db.Instant(r.UserUpdatedAtMs),
        })
        .FirstOrDefault();

    public Task SetAutomaticAsync(long assetId, CullingOutcome outcome, IReadOnlyList<string> reasons, string modelVersion, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Decisions (AssetId, AutomaticOutcome, ReasonsJson, ModelVersion, UpdatedAtMs) VALUES (@assetId, @outcome, @reasons, @modelVersion, @now)
            ON CONFLICT (AssetId) DO UPDATE SET AutomaticOutcome = excluded.AutomaticOutcome, ReasonsJson = excluded.ReasonsJson,
                                                ModelVersion = excluded.ModelVersion, UpdatedAtMs = excluded.UpdatedAtMs;
            """,
            new { assetId, outcome = outcome.ToString(), reasons = JsonSerializer.Serialize(reasons), modelVersion, now = Db.Now() },
            cancellationToken: ct)), ct);

    public Task SetUserAsync(long assetId, CullingOutcome? outcome, int? rating, string? label, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Decisions (AssetId, UserOutcome, Rating, Label, UpdatedAtMs, UserUpdatedAtMs) VALUES (@assetId, @outcome, @rating, @label, @now, @now)
            ON CONFLICT (AssetId) DO UPDATE SET UserOutcome = excluded.UserOutcome, Rating = excluded.Rating, Label = excluded.Label,
                                                UpdatedAtMs = excluded.UpdatedAtMs, UserUpdatedAtMs = excluded.UserUpdatedAtMs;
            """,
            new { assetId, outcome = Db.Name(outcome), rating, label, now = Db.Now() },
            cancellationToken: ct)), ct);

    public async Task<IReadOnlyDictionary<CullingOutcome, int>> CountOutcomesForJobAsync(long jobId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<(string Outcome, int Count)>(new CommandDefinition(
            """
            SELECT COALESCE(d.UserOutcome, d.AutomaticOutcome) AS Outcome, COUNT(*) AS Count
            FROM Decisions d
            WHERE d.AssetId IN (SELECT DISTINCT AssetId FROM JobItems WHERE JobId = @jobId AND AssetId IS NOT NULL)
              AND COALESCE(d.UserOutcome, d.AutomaticOutcome) IS NOT NULL
            GROUP BY Outcome;
            """,
            new { jobId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .ToDictionary(r => Db.Parse<CullingOutcome>(r.Outcome), r => r.Count);

    private sealed class Row
    {
        public long Id { get; set; }

        public long AssetId { get; set; }

        public string? AutomaticOutcome { get; set; }

        public string ReasonsJson { get; set; } = "[]";

        public string? ModelVersion { get; set; }

        public string? UserOutcome { get; set; }

        public long? UserUpdatedAtMs { get; set; }

        public int? Rating { get; set; }

        public string? Label { get; set; }
    }
}

internal sealed class XmpSyncStore(SqliteCatalog catalog) : IXmpSyncStore
{
    public async Task<XmpSyncState?> GetAsync(long assetId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<Row>(new CommandDefinition(
            "SELECT AssetId, LastWrittenMs, XmpSignature, WritePending, LastError, WrittenFlatJson, WrittenHierarchicalJson, FileStampJson, SnapshotJson FROM XmpSync WHERE AssetId = @assetId;", new { assetId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new XmpSyncState(r.AssetId, Db.Instant(r.LastWrittenMs), r.XmpSignature, r.WritePending, r.LastError)
        {
            FileStampJson = r.FileStampJson,
            SnapshotJson = r.SnapshotJson,
            WrittenFlat = r.WrittenFlatJson is { } flat ? System.Text.Json.JsonSerializer.Deserialize<string[]>(flat) ?? [] : [],
            WrittenHierarchical = r.WrittenHierarchicalJson is { } hierarchical ? System.Text.Json.JsonSerializer.Deserialize<string[]>(hierarchical) ?? [] : [],
        })
        .FirstOrDefault();

    public Task MarkPendingAsync(long assetId, string? error, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO XmpSync (AssetId, WritePending, LastError) VALUES (@assetId, 1, @error)
            ON CONFLICT (AssetId) DO UPDATE SET WritePending = 1, LastError = excluded.LastError;
            """,
            new { assetId, error }, cancellationToken: ct)), ct);

    public Task MarkWrittenAsync(long assetId, string signature, CancellationToken ct, IReadOnlyList<string>? flatKeywords = null, IReadOnlyList<string>? hierarchicalKeywords = null) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO XmpSync (AssetId, LastWrittenMs, XmpSignature, WritePending, LastError, WrittenFlatJson, WrittenHierarchicalJson)
            VALUES (@assetId, @now, @signature, 0, NULL, @flat, @hierarchical)
            ON CONFLICT (AssetId) DO UPDATE SET LastWrittenMs = excluded.LastWrittenMs, XmpSignature = excluded.XmpSignature, WritePending = 0, LastError = NULL,
                WrittenFlatJson = excluded.WrittenFlatJson, WrittenHierarchicalJson = excluded.WrittenHierarchicalJson;
            """,
            new
            {
                assetId, signature, now = Db.Now(),
                flat = System.Text.Json.JsonSerializer.Serialize(flatKeywords ?? []),
                hierarchical = System.Text.Json.JsonSerializer.Serialize(hierarchicalKeywords ?? []),
            }, cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<long>> ListPendingAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<long>(new CommandDefinition(
            "SELECT AssetId FROM XmpSync WHERE WritePending = 1 ORDER BY AssetId;", cancellationToken: ct)), ct).ConfigureAwait(false)).ToList();

    public Task RecordFileAsync(long assetId, string? fileStampJson, string snapshotJson, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO XmpSync (AssetId, FileStampJson, SnapshotJson) VALUES (@assetId, @fileStampJson, @snapshotJson)
            ON CONFLICT (AssetId) DO UPDATE SET FileStampJson = excluded.FileStampJson, SnapshotJson = excluded.SnapshotJson;
            """,
            new { assetId, fileStampJson, snapshotJson }, cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<long>> ListRecordedAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<long>(new CommandDefinition(
            "SELECT AssetId FROM XmpSync WHERE SnapshotJson IS NOT NULL ORDER BY AssetId;", cancellationToken: ct)), ct).ConfigureAwait(false)).ToList();

    public Task AddLogAsync(IReadOnlyList<XmpSyncLogEntry> entries, CancellationToken ct) =>
        catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            foreach (var e in entries)
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO XmpSyncLog (AssetId, AtMs, Field, Value, Winner, Note) VALUES (@AssetId, @at, @Field, @Value, @winner, @Note);",
                    new { e.AssetId, at = e.At.ToUnixTimeMilliseconds(), e.Field, e.Value, winner = e.Winner.ToString(), e.Note }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            }

            transaction.Commit();
        }, ct);

    public async Task<IReadOnlyList<XmpSyncLogEntry>> ListLogAsync(long? assetId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<(long AssetId, long AtMs, string Field, string? Value, string Winner, string? Note)>(new CommandDefinition(
            "SELECT AssetId, AtMs, Field, Value, Winner, Note FROM XmpSyncLog WHERE @assetId IS NULL OR AssetId = @assetId ORDER BY Id;",
            new { assetId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new XmpSyncLogEntry(r.AssetId, DateTimeOffset.FromUnixTimeMilliseconds(r.AtMs), r.Field, r.Value, Db.Parse<XmpSyncWinner>(r.Winner), r.Note))
        .ToList();

    private sealed class Row
    {
        public long AssetId { get; set; }

        public long? LastWrittenMs { get; set; }

        public string? XmpSignature { get; set; }

        public bool WritePending { get; set; }

        public string? LastError { get; set; }

        public string? WrittenFlatJson { get; set; }

        public string? WrittenHierarchicalJson { get; set; }

        public string? FileStampJson { get; set; }

        public string? SnapshotJson { get; set; }
    }
}

internal sealed class DeferredCopyStore(SqliteCatalog catalog) : IDeferredCopyStore
{
    private const string Columns =
        "Id, JobId, SourcePath, DestinationId, DestinationRoot, TargetRelativePath, ContentHash, Attempts, NextAttemptMs, State, LastError";

    public Task EnqueueAsync(DeferredCopy copy, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT OR IGNORE INTO DeferredCopies (JobId, SourcePath, DestinationId, DestinationRoot, TargetRelativePath, ContentHash, Attempts, NextAttemptMs, State, LastError)
            VALUES (@JobId, @SourcePath, @DestinationId, @DestinationRoot, @TargetRelativePath, @ContentHash, 0, @NextAttemptMs, 'Pending', NULL);
            """,
            new
            {
                copy.JobId,
                copy.SourcePath,
                copy.DestinationId,
                copy.DestinationRoot,
                copy.TargetRelativePath,
                copy.ContentHash,
                NextAttemptMs = Db.Ms(copy.NextAttemptAt),
            },
            cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<DeferredCopy>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM DeferredCopies WHERE State = 'Pending' AND NextAttemptMs <= @now ORDER BY NextAttemptMs, Id LIMIT @limit;",
            new { now = Db.Ms(now), limit }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(Map).ToList();

    public Task UpdateAsync(DeferredCopy copy, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            "UPDATE DeferredCopies SET Attempts = @Attempts, NextAttemptMs = @NextAttemptMs, State = @State, LastError = @LastError WHERE Id = @Id;",
            new { copy.Id, copy.Attempts, NextAttemptMs = Db.Ms(copy.NextAttemptAt), State = copy.State.ToString(), copy.LastError },
            cancellationToken: ct)), ct);

    public Task<int> CountPendingAsync(CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM DeferredCopies WHERE State = 'Pending';", cancellationToken: ct)), ct);

    private static DeferredCopy Map(Row r) =>
        new(r.Id, r.JobId, r.SourcePath, r.DestinationId, r.DestinationRoot, r.TargetRelativePath, r.ContentHash, r.Attempts,
            Db.Instant(r.NextAttemptMs)!.Value, Db.Parse<DeferredCopyState>(r.State), r.LastError);

    private sealed class Row
    {
        public long Id { get; set; }

        public long? JobId { get; set; }

        public string SourcePath { get; set; } = "";

        public string DestinationId { get; set; } = "";

        public string DestinationRoot { get; set; } = "";

        public string TargetRelativePath { get; set; } = "";

        public string ContentHash { get; set; } = "";

        public int Attempts { get; set; }

        public long NextAttemptMs { get; set; }

        public string State { get; set; } = "";

        public string? LastError { get; set; }
    }
}
