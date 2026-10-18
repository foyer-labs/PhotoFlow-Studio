// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PhotoFlow.Data;

public sealed record CatalogOpenOptions(string CatalogPath, string BackupsDirectory);

/// <summary>A numbered, forward-only SQL script (<c>NNNN_description.sql</c>).</summary>
public sealed record CatalogMigration(int Version, string ScriptName, string Sql);

/// <summary>
/// Applies pending migrations in order, each in its own transaction. Before touching an existing catalog it
/// takes a consistent backup with the SQLite online backup API (CLAUDE.md §4).
/// </summary>
public static partial class CatalogMigrator
{
    /// <summary>Scripts embedded in this assembly (Migrations/*.sql).</summary>
    public static IReadOnlyList<CatalogMigration> Embedded()
    {
        var assembly = typeof(CatalogMigrator).Assembly;
        return assembly.GetManifestResourceNames()
            .Select(name => (Name: name, Match: ScriptName().Match(name)))
            .Where(r => r.Match.Success)
            .Select(r =>
            {
                using var stream = assembly.GetManifestResourceStream(r.Name)!;
                using var reader = new StreamReader(stream);
                return new CatalogMigration(int.Parse(r.Match.Groups[1].Value, CultureInfo.InvariantCulture), r.Match.Groups[0].Value, reader.ReadToEnd());
            })
            .OrderBy(m => m.Version)
            .ToList();
    }

    /// <summary>Applies the pending migrations; returns the versions applied.</summary>
    public static async Task<IReadOnlyList<int>> ApplyAsync(CatalogOpenOptions options, IReadOnlyList<CatalogMigration> migrations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(migrations);
        if (migrations.Select(m => m.Version).Distinct().Count() != migrations.Count)
        {
            throw new ArgumentException("Duplicate migration versions.", nameof(migrations));
        }

        var existed = File.Exists(options.CatalogPath) && new FileInfo(options.CatalogPath).Length > 0;
        var current = existed ? await ReadVersionAsync(options.CatalogPath, ct).ConfigureAwait(false) : 0;
        var pending = migrations.Where(m => m.Version > current).OrderBy(m => m.Version).ToList();
        if (pending.Count == 0)
        {
            return [];
        }

        if (existed)
        {
            await BackupAsync(options, current, ct).ConfigureAwait(false);
        }

        await using var connection = Connections.Open(options.CatalogPath);
        await Connections.ExecuteAsync(
            connection,
            "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER PRIMARY KEY, ScriptName TEXT NOT NULL, AppliedAtMs INTEGER NOT NULL);",
            ct).ConfigureAwait(false);

        var applied = new List<int>();
        foreach (var migration in pending)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await Connections.ExecuteAsync(connection, migration.Sql, ct, transaction).ConfigureAwait(false);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO SchemaVersion (Version, ScriptName, AppliedAtMs) VALUES ($v, $n, $t);";
                insert.Parameters.AddWithValue("$v", migration.Version);
                insert.Parameters.AddWithValue("$n", migration.ScriptName);
                insert.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            applied.Add(migration.Version);
        }

        return applied;
    }

    /// <summary>Schema version of a catalog file (0 when it has no schema yet).</summary>
    public static async Task<int> ReadVersionAsync(string catalogPath, CancellationToken ct)
    {
        await using var connection = Connections.Open(catalogPath, readOnly: true);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'SchemaVersion';";
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
        {
            return 0;
        }

        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task BackupAsync(CatalogOpenOptions options, int version, CancellationToken ct)
    {
        Directory.CreateDirectory(options.BackupsDirectory);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = $"{Path.GetFileNameWithoutExtension(options.CatalogPath)}-v{version}-{stamp}{Path.GetExtension(options.CatalogPath)}";
        var target = Path.Combine(options.BackupsDirectory, name);
        for (var n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(options.BackupsDirectory, $"{Path.GetFileNameWithoutExtension(name)}-{n}{Path.GetExtension(name)}");
        }

        await using var source = Connections.Open(options.CatalogPath, readOnly: true);
        await using var destination = Connections.Open(target);
        ct.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
    }

    [GeneratedRegex(@"(\d{4})_[A-Za-z0-9_]+\.sql$")]
    private static partial Regex ScriptName();
}

/// <summary>Journal mode by location (spec §9): WAL does not work on network file systems.</summary>
public static class CatalogJournal
{
    public static string ModeFor(string catalogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        if (catalogPath.StartsWith(@"\\", StringComparison.Ordinal) || catalogPath.StartsWith("//", StringComparison.Ordinal))
        {
            return "DELETE";
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(catalogPath));
                if (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network)
                {
                    return "DELETE";
                }
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // Unknown drive: treat as local.
            }
        }

        return "WAL";
    }
}

internal static class Connections
{
    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 30,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 10000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
