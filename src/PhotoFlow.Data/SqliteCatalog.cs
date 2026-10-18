// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Data;

/// <summary>
/// SQLite catalog (spec §9): one PC at a time (lock file held open exclusively), integrity check on open,
/// forward-only migrations with backup, WAL locally and rollback journal on network paths. A single connection
/// serialized by a semaphore: SQLite serializes writes anyway and this avoids busy errors.
/// </summary>
public sealed class SqliteCatalog : ICatalog
{
    private readonly SqliteConnection _connection;
    private readonly FileStream _lock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _catalogPath;

    private SqliteCatalog(SqliteConnection connection, FileStream lockStream, string catalogPath)
    {
        _connection = connection;
        _catalogPath = catalogPath;
        _lock = lockStream;
        Libraries = new LibraryStore(this);
        Assets = new AssetStore(this);
        Jobs = new JobStore(this);
        Decisions = new DecisionStore(this);
        XmpSync = new XmpSyncStore(this);
        DeferredCopies = new DeferredCopyStore(this);
        Analysis = new AnalysisStore(this);
        Groups = new GroupStore(this);
        People = new PeopleStore(this);
        Labels = new LabelStore(this);
        Tags = new TagStore(this);
        Search = new SqliteSearchIndex(this);
        Duplicates = new DuplicateStore(this);
        Motorsport = new MotorsportStore(this);
        Captions = new CaptionStore(this);
        Reanalysis = new ReanalysisStore(this);
    }

    public ILibraryStore Libraries { get; }

    public IAssetStore Assets { get; }

    public IJobStore Jobs { get; }

    public IDecisionStore Decisions { get; }

    public IXmpSyncStore XmpSync { get; }

    public IDeferredCopyStore DeferredCopies { get; }

    public IAnalysisStore Analysis { get; }

    public IGroupStore Groups { get; }

    public IPeopleStore People { get; }

    public ILabelStore Labels { get; }

    public ITagStore Tags { get; }

    public Core.Search.ISearchIndex Search { get; }

    public IDuplicateStore Duplicates { get; }

    public IMotorsportStore Motorsport { get; }

    public ICaptionStore Captions { get; }

    public IReanalysisStore Reanalysis { get; }

    public static async Task<SqliteCatalog> OpenAsync(CatalogOpenOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.CatalogPath))!);
        var lockStream = await AcquireLockAsync(options.CatalogPath, ct).ConfigureAwait(false);
        try
        {
            await CheckIntegrityAsync(options.CatalogPath, ct).ConfigureAwait(false);
            await CatalogMigrator.ApplyAsync(options, CatalogMigrator.Embedded(), ct).ConfigureAwait(false);
            var connection = Connections.Open(options.CatalogPath);
            await Connections.ExecuteAsync(connection, $"PRAGMA journal_mode = {CatalogJournal.ModeFor(options.CatalogPath)};", ct).ConfigureAwait(false);
            return new SqliteCatalog(connection, lockStream, Path.GetFullPath(options.CatalogPath));
        }
        catch
        {
            await lockStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Replaces the catalog with a backup (spec §9: restore proposed from the last backup). Runs with the catalog lock
    /// held and closed: the backup is checked first, the current catalog with its journal files is moved (never deleted)
    /// into a <c>replaced-*</c> folder next to the backups, then the backup is copied in its place. Returns that folder.
    /// </summary>
    /// <exception cref="CatalogLockedException">Another window uses the catalog.</exception>
    /// <exception cref="CatalogCorruptException">The backup itself is damaged; nothing was changed.</exception>
    public static async Task<string> RestoreAsync(CatalogOpenOptions options, string backupPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(backupPath) || new FileInfo(backupPath).Length == 0)
        {
            throw new CatalogCorruptException($"The backup {backupPath} is missing or empty.");
        }

        var catalogPath = Path.GetFullPath(options.CatalogPath);
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        await using var lockStream = await AcquireLockAsync(catalogPath, ct).ConfigureAwait(false);
        await CheckIntegrityAsync(backupPath, ct).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var replaced = Path.Combine(options.BackupsDirectory, $"replaced-{stamp}");
        for (var n = 2; Directory.Exists(replaced); n++)
        {
            replaced = Path.Combine(options.BackupsDirectory, $"replaced-{stamp}-{n}");
        }

        Directory.CreateDirectory(replaced);
        foreach (var file in new[] { catalogPath, catalogPath + "-wal", catalogPath + "-shm", catalogPath + "-journal" }.Where(File.Exists))
        {
            File.Move(file, Path.Combine(replaced, Path.GetFileName(file)));
        }

        File.Copy(backupPath, catalogPath);
        return replaced;
    }

    /// <summary>
    /// Copies a closed catalog to a new place (spec §9: the catalog can be moved). The source lock is held during the
    /// copy, the source is checked first and copied with SQLite's backup (journal files included); the source stays
    /// where it is. Returns the full path of the copy.
    /// </summary>
    /// <exception cref="CatalogLockedException">The catalog is open in another window or on another PC.</exception>
    /// <exception cref="CatalogCorruptException">The source is damaged; nothing was copied.</exception>
    /// <exception cref="IOException">A file already exists at <paramref name="destinationPath"/>.</exception>
    public static async Task<string> CopyAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (File.Exists(destination))
        {
            throw new IOException($"{destination} already exists.");
        }

        await using var lockStream = await AcquireLockAsync(source, ct).ConfigureAwait(false);
        await CheckIntegrityAsync(source, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var from = Connections.Open(source, readOnly: true))
        await using (var to = Connections.Open(destination))
        {
            ct.ThrowIfCancellationRequested();
            from.BackupDatabase(to);
        }

        await CheckIntegrityAsync(destination, ct).ConfigureAwait(false);
        return destination;
    }

    /// <summary>True when <paramref name="path"/> is a PhotoFlow catalog in good health (read only, nothing changed).</summary>
    public static async Task<bool> IsCatalogAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return false;
        }

        try
        {
            await CheckIntegrityAsync(path, ct).ConfigureAwait(false);
            await using var connection = Connections.Open(path, readOnly: true);
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('SchemaVersion', 'Assets', 'Libraries');", cancellationToken: ct)).ConfigureAwait(false) == 3;
        }
        catch (CatalogCorruptException)
        {
            return false;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken ct) =>
        await RunAsync(c => c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;", cancellationToken: ct)), ct).ConfigureAwait(false);

    public Task<string> BackupAsync(string backupsDirectory, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupsDirectory);
        Directory.CreateDirectory(backupsDirectory);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = Path.GetFileNameWithoutExtension(_catalogPath);
        var extension = Path.GetExtension(_catalogPath);
        var target = Path.Combine(backupsDirectory, $"{name}-{reason}-{stamp}{extension}");
        for (var n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(backupsDirectory, $"{name}-{reason}-{stamp}-{n}{extension}");
        }

        return RunAsync(async c =>
        {
            await using var destination = Connections.Open(target);
            ct.ThrowIfCancellationRequested();
            c.BackupDatabase(destination);
            return target;
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            await _lock.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    internal async Task<T> RunAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action(_connection).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal Task RunAsync(Func<SqliteConnection, Task> action, CancellationToken ct) =>
        RunAsync(async c =>
        {
            await action(c).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>Attempts to take the lock: a process that just ended (crash, forced close) may release it a moment late.</summary>
    public static readonly TimeSpan LockRetryWindow = TimeSpan.FromSeconds(2);

    private static async Task<FileStream> AcquireLockAsync(string catalogPath, CancellationToken ct)
    {
        var lockPath = catalogPath + ".lock";
        var deadline = DateTime.UtcNow + LockRetryWindow;
        while (true)
        {
            try
            {
                // Held open without sharing for the whole session: released by the OS even after a crash, never deleted.
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                stream.SetLength(0);
                var owner = Encoding.UTF8.GetBytes($"{Environment.MachineName} pid {Environment.ProcessId} {DateTimeOffset.Now:O}");
                stream.Write(owner);
                stream.Flush(flushToDisk: true);
                return stream;
            }
            catch (IOException e) when (DateTime.UtcNow >= deadline)
            {
                var holder = TryReadOwner(lockPath);
                throw new CatalogLockedException($"The catalog is open on another PC or process{(holder is null ? "" : $" ({holder})")}: {e.Message}");
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
            }
        }
    }

    private static string? TryReadOwner(string lockPath)
    {
        try
        {
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task CheckIntegrityAsync(string catalogPath, CancellationToken ct)
    {
        if (!File.Exists(catalogPath) || new FileInfo(catalogPath).Length == 0)
        {
            return;
        }

        try
        {
            await using var connection = Connections.Open(catalogPath, readOnly: true);
            var result = await connection.ExecuteScalarAsync<string>(new CommandDefinition("PRAGMA quick_check;", cancellationToken: ct)).ConfigureAwait(false);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new CatalogCorruptException($"Catalog integrity check failed: {result}");
            }
        }
        catch (SqliteException e)
        {
            throw new CatalogCorruptException($"The catalog file is damaged or not a catalog: {e.Message}");
        }
    }
}

/// <summary>Value conversions shared by the stores (instants as Unix milliseconds, enums as names).</summary>
internal static class Db
{
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static long? Ms(DateTimeOffset? value) => value?.ToUnixTimeMilliseconds();

    public static DateTimeOffset? Instant(long? ms, long? offsetMinutes = null) =>
        ms is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms.Value).ToOffset(TimeSpan.FromMinutes(offsetMinutes ?? 0));

    public static string? Name<TEnum>(TEnum? value)
        where TEnum : struct, Enum => value?.ToString();

    public static TEnum Parse<TEnum>(string value)
        where TEnum : struct, Enum => Enum.Parse<TEnum>(value);

    public static TEnum? ParseOrNull<TEnum>(string? value)
        where TEnum : struct, Enum => value is null ? null : Enum.Parse<TEnum>(value);

    public static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? Date(string? value) =>
        value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? Box(BoundingBox? box) =>
        box is { } b ? string.Join(' ', new[] { b.X1, b.Y1, b.X2, b.Y2 }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) : null;

    public static BoundingBox? Box(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var v = value.Split(' ').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
        return new BoundingBox(v[0], v[1], v[2], v[3]);
    }
}
