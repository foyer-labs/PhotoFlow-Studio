// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Dapper;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Data;

internal sealed class MotorsportStore(SqliteCatalog catalog) : IMotorsportStore
{
    public Task<long> CreateListAsync(string name, string? sourceFile, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO EntryLists (Name, SourceFile, CreatedAtMs) VALUES (@name, @sourceFile, @now) RETURNING Id;",
            new { name, sourceFile, now = Db.Now() }, cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<EntryList>> ListListsAsync(CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<(long Id, string Name, string? SourceFile, long CreatedAtMs)>(new CommandDefinition(
            "SELECT Id, Name, SourceFile, CreatedAtMs FROM EntryLists ORDER BY CreatedAtMs DESC;", cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new EntryList(r.Id, r.Name, r.SourceFile, DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAtMs))).ToList();

    public Task ReplaceEntriesAsync(long listId, IReadOnlyList<Entry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM Entries WHERE ListId = @listId;", new { listId }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            foreach (var entry in entries.DistinctBy(e => e.Number))
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO Entries (ListId, Number, DriversJson, Team, Category, Car) VALUES (@listId, @Number, @drivers, @Team, @Category, @Car);",
                    new { listId, entry.Number, drivers = JsonSerializer.Serialize(entry.Drivers), entry.Team, entry.Category, entry.Car }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            }

            transaction.Commit();
        }, ct);
    }

    public async Task<IReadOnlyList<Entry>> GetEntriesAsync(long listId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<EntryRow>(new CommandDefinition(
            "SELECT Id, ListId, Number, DriversJson, Team, Category, Car FROM Entries WHERE ListId = @listId ORDER BY length(Number), Number;",
            new { listId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new Entry(r.Id, r.ListId, r.Number, JsonSerializer.Deserialize<string[]>(r.DriversJson) ?? [], r.Team, r.Category, r.Car)).ToList();

    public Task SetJobListAsync(long jobId, long? listId, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            listId is null
                ? "DELETE FROM JobEntryLists WHERE JobId = @jobId;"
                : "INSERT INTO JobEntryLists (JobId, ListId) VALUES (@jobId, @listId) ON CONFLICT (JobId) DO UPDATE SET ListId = excluded.ListId;",
            new { jobId, listId }, cancellationToken: ct)), ct);

    public Task<long?> GetJobListAsync(long jobId, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT ListId FROM JobEntryLists WHERE JobId = @jobId;", new { jobId }, cancellationToken: ct)), ct);

    public Task ReplaceReadingsAsync(long assetId, IReadOnlyList<OcrReading> readings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readings);
        return catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM OcrResults WHERE AssetId = @assetId;", new { assetId }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            foreach (var r in readings)
            {
                await c.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO OcrResults (AssetId, Vehicle, VX1, VY1, VX2, VY2, X1, Y1, X2, Y2, Text, Confidence, ModelVersion)
                    VALUES (@assetId, @Vehicle, @vx1, @vy1, @vx2, @vy2, @x1, @y1, @x2, @y2, @Text, @Confidence, @ModelVersion);
                    """,
                    new
                    {
                        assetId, r.Vehicle, vx1 = r.VehicleBox.X1, vy1 = r.VehicleBox.Y1, vx2 = r.VehicleBox.X2, vy2 = r.VehicleBox.Y2,
                        x1 = r.Box.X1, y1 = r.Box.Y1, x2 = r.Box.X2, y2 = r.Box.Y2, r.Text, r.Confidence, r.ModelVersion,
                    }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            }

            transaction.Commit();
        }, ct);
    }

    public async Task<IReadOnlyList<OcrReading>> GetReadingsAsync(long assetId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<ReadingRow>(new CommandDefinition(
            "SELECT AssetId, Vehicle, VX1, VY1, VX2, VY2, X1, Y1, X2, Y2, Text, Confidence, ModelVersion FROM OcrResults WHERE AssetId = @assetId ORDER BY Vehicle, Id;",
            new { assetId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(r => new OcrReading(r.AssetId, r.Vehicle, new BoundingBox((float)r.VX1, (float)r.VY1, (float)r.VX2, (float)r.VY2),
            new BoundingBox((float)r.X1, (float)r.Y1, (float)r.X2, (float)r.Y2), r.Text, r.Confidence, r.ModelVersion)).ToList();

    public Task ReplaceAutomaticNumbersAsync(long assetId, IReadOnlyList<VehicleNumber> numbers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        return catalog.RunAsync(async c =>
        {
            using var transaction = c.BeginTransaction();
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM VehicleNumbers WHERE AssetId = @assetId AND Origin <> 'Manual';", new { assetId }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            foreach (var n in numbers.Where(n => n.Origin != NumberOrigin.Manual))
            {
                // A vehicle the user already decided stays as it is (I5).
                await c.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO VehicleNumbers (AssetId, Vehicle, X1, Y1, X2, Y2, Number, State, Origin, CandidatesJson, UpdatedAtMs)
                    VALUES (@assetId, @Vehicle, @x1, @y1, @x2, @y2, @Number, @state, @origin, @candidates, @now)
                    ON CONFLICT (AssetId, Vehicle) DO NOTHING;
                    """,
                    new
                    {
                        assetId, n.Vehicle, x1 = n.Box.X1, y1 = n.Box.Y1, x2 = n.Box.X2, y2 = n.Box.Y2, n.Number, state = n.State.ToString(), origin = n.Origin.ToString(),
                        candidates = JsonSerializer.Serialize(n.Candidates), now = Db.Now(),
                    }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            }

            transaction.Commit();
        }, ct);
    }

    public Task SetManualNumberAsync(long assetId, int vehicle, string? number, CancellationToken ct) =>
        catalog.RunAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO VehicleNumbers (AssetId, Vehicle, X1, Y1, X2, Y2, Number, State, Origin, CandidatesJson, UpdatedAtMs)
            VALUES (@assetId, @vehicle, 0, 0, 1, 1, @number, @state, 'Manual', '[]', @now)
            ON CONFLICT (AssetId, Vehicle) DO UPDATE SET Number = excluded.Number, State = excluded.State, Origin = 'Manual', UpdatedAtMs = excluded.UpdatedAtMs;
            """,
            new { assetId, vehicle, number, state = (number is null ? NumberState.NoNumber : NumberState.Certain).ToString(), now = Db.Now() }, cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<VehicleNumber>> GetNumbersAsync(long assetId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<NumberRow>(new CommandDefinition(
            "SELECT AssetId, Vehicle, X1, Y1, X2, Y2, Number, State, Origin, CandidatesJson FROM VehicleNumbers WHERE AssetId = @assetId ORDER BY Vehicle;",
            new { assetId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(ToNumber).ToList();

    public async Task<IReadOnlyList<VehicleNumber>> GetJobNumbersAsync(long jobId, CancellationToken ct) =>
        (await catalog.RunAsync(c => c.QueryAsync<NumberRow>(new CommandDefinition(
            """
            SELECT AssetId, Vehicle, X1, Y1, X2, Y2, Number, State, Origin, CandidatesJson FROM VehicleNumbers
            WHERE AssetId IN (SELECT AssetId FROM JobItems WHERE JobId = @jobId AND AssetId IS NOT NULL) ORDER BY AssetId, Vehicle;
            """,
            new { jobId }, cancellationToken: ct)), ct).ConfigureAwait(false))
        .Select(ToNumber).ToList();

    public Task PutNumberAsync(long assetId, int vehicle, VehicleNumber? number, CancellationToken ct) =>
        catalog.RunAsync(c => number is null
            ? c.ExecuteAsync(new CommandDefinition("DELETE FROM VehicleNumbers WHERE AssetId = @assetId AND Vehicle = @vehicle;", new { assetId, vehicle }, cancellationToken: ct))
            : c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO VehicleNumbers (AssetId, Vehicle, X1, Y1, X2, Y2, Number, State, Origin, CandidatesJson, UpdatedAtMs)
                VALUES (@assetId, @vehicle, @x1, @y1, @x2, @y2, @Number, @state, @origin, @candidates, @now)
                ON CONFLICT (AssetId, Vehicle) DO UPDATE SET X1 = excluded.X1, Y1 = excluded.Y1, X2 = excluded.X2, Y2 = excluded.Y2, Number = excluded.Number,
                    State = excluded.State, Origin = excluded.Origin, CandidatesJson = excluded.CandidatesJson, UpdatedAtMs = excluded.UpdatedAtMs;
                """,
                new
                {
                    assetId, vehicle, x1 = number.Box.X1, y1 = number.Box.Y1, x2 = number.Box.X2, y2 = number.Box.Y2, number.Number, state = number.State.ToString(),
                    origin = number.Origin.ToString(), candidates = JsonSerializer.Serialize(number.Candidates), now = Db.Now(),
                }, cancellationToken: ct)), ct);

    private static VehicleNumber ToNumber(NumberRow r) =>
        new(r.AssetId, r.Vehicle, new BoundingBox((float)r.X1, (float)r.Y1, (float)r.X2, (float)r.Y2), r.Number,
            Db.Parse<NumberState>(r.State), Db.Parse<NumberOrigin>(r.Origin), JsonSerializer.Deserialize<NumberCandidate[]>(r.CandidatesJson) ?? []);

    private sealed class EntryRow
    {
        public long Id { get; set; }

        public long ListId { get; set; }

        public string Number { get; set; } = string.Empty;

        public string DriversJson { get; set; } = "[]";

        public string? Team { get; set; }

        public string? Category { get; set; }

        public string? Car { get; set; }
    }

    private sealed class ReadingRow
    {
        public long AssetId { get; set; }

        public int Vehicle { get; set; }

        public double VX1 { get; set; }

        public double VY1 { get; set; }

        public double VX2 { get; set; }

        public double VY2 { get; set; }

        public double X1 { get; set; }

        public double Y1 { get; set; }

        public double X2 { get; set; }

        public double Y2 { get; set; }

        public string Text { get; set; } = string.Empty;

        public double Confidence { get; set; }

        public string ModelVersion { get; set; } = string.Empty;
    }

    private sealed class NumberRow
    {
        public long AssetId { get; set; }

        public int Vehicle { get; set; }

        public double X1 { get; set; }

        public double Y1 { get; set; }

        public double X2 { get; set; }

        public double Y2 { get; set; }

        public string? Number { get; set; }

        public string State { get; set; } = string.Empty;

        public string Origin { get; set; } = string.Empty;

        public string CandidatesJson { get; set; } = "[]";
    }
}
