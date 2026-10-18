// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using PhotoFlow.Core.Paths;
using PhotoFlow.Data;

namespace PhotoFlow.App.Services;

/// <summary>A catalog backup offered for restore (board 5h).</summary>
public sealed record CatalogBackup(string Path, DateTimeOffset CreatedAt, long Bytes);

/// <summary>What happened to a restore applied at startup: restored (old catalog kept in <paramref name="ReplacedFolder"/>) or refused.</summary>
public sealed record CatalogRestoreOutcome(string Backup, string? ReplacedFolder, string? Error)
{
    public bool Restored => Error is null;
}

/// <summary>
/// Restore of the catalog from one of its backups (spec §9). The catalog is open while PhotoFlow runs, so a restore
/// chosen in the settings is recorded and applied at the next start, before the catalog opens; at startup with a
/// damaged catalog it is applied at once. The replaced catalog is moved next to the backups, never deleted.
/// </summary>
public sealed class CatalogRestoreService(IAppPaths paths)
{
    public const string MarkerName = "restore.pending";

    private string MarkerPath => System.IO.Path.Combine(paths.ConfigDirectory, MarkerName);

    /// <summary>Backups of the catalog, newest first (only the top level of the backups folder).</summary>
    public IReadOnlyList<CatalogBackup> ListBackups()
    {
        var extension = System.IO.Path.GetExtension(paths.CatalogPath);
        return Directory.Exists(paths.BackupsDirectory)
            ? new DirectoryInfo(paths.BackupsDirectory).EnumerateFiles("*" + extension, SearchOption.TopDirectoryOnly)
                .Where(f => f.Length > 0)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new CatalogBackup(f.FullName, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime(), f.Length))
                .ToList()
            : [];
    }

    /// <summary>The backup chosen for the next start, if any.</summary>
    public string? Pending => File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() is { Length: > 0 } path ? path : null : null;

    /// <summary>Records <paramref name="backup"/> to be restored at the next start; only a backup of this catalog is accepted.</summary>
    public void Request(string backup)
    {
        if (!ListBackups().Any(b => string.Equals(b.Path, System.IO.Path.GetFullPath(backup), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{backup} is not a backup of this catalog.");
        }

        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(MarkerPath, System.IO.Path.GetFullPath(backup));
    }

    /// <summary>Withdraws a pending restore (the marker is moved to the temporary folder, cleaned after a day).</summary>
    public void Cancel() => Retire("cancelled");

    /// <summary>Applies the pending restore before the catalog opens; returns the folder holding the replaced catalog.</summary>
    public async Task<string?> ApplyPendingAsync(CancellationToken ct)
    {
        if (Pending is not { } backup)
        {
            return null;
        }

        var replaced = await RestoreNowAsync(backup, ct).ConfigureAwait(false);
        Retire("done");
        return replaced;
    }

    /// <summary>Restores <paramref name="backup"/> immediately; the catalog must not be open.</summary>
    public Task<string> RestoreNowAsync(string backup, CancellationToken ct) =>
        SqliteCatalog.RestoreAsync(new CatalogOpenOptions(paths.CatalogPath, paths.BackupsDirectory), backup, ct);

    private void Retire(string reason)
    {
        if (!File.Exists(MarkerPath))
        {
            return;
        }

        Directory.CreateDirectory(paths.TempDirectory);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        File.Move(MarkerPath, System.IO.Path.Combine(paths.TempDirectory, $"restore-{reason}-{stamp}.txt"), overwrite: true);
    }
}
