// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Paths;

namespace PhotoFlow.App.Services;

/// <summary>What a maintenance pass did, for the log.</summary>
public sealed record MaintenanceReport(string? Backup, int BackupsRemoved, int ThumbnailsRemoved, int TempRemoved, int DownloadsRemoved);

/// <summary>
/// Rules for PhotoFlow's internal files (spec §10, decision B1), all deletions through <see cref="InternalFileCleaner"/>:
/// catalog backup at close and at least daily, the last <see cref="KeepBackups"/> kept; thumbnail cache within
/// <see cref="ThumbnailCacheLimit"/> (oldest first, rebuilt on demand); temporary files older than a day; GPU pack
/// archives once the pack is installed and verified. User files are never touched. The backup chosen for a pending
/// restore (<paramref name="protectedBackup"/>) is never rotated away.
/// </summary>
public sealed class MaintenanceService(IAppPaths paths, InternalFileCleaner cleaner, TimeProvider? time = null, Func<string?>? protectedBackup = null)
{
    public const int KeepBackups = 10;
    public const long ThumbnailCacheLimit = 4L * 1024 * 1024 * 1024;
    public static readonly TimeSpan DailyBackup = TimeSpan.FromDays(1);
    public static readonly TimeSpan TempMaxAge = TimeSpan.FromDays(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Startup pass: daily backup if due, then the cleanup rules.</summary>
    public async Task<MaintenanceReport> RunAtStartupAsync(ICatalog catalog, string? gpuDownloads, bool gpuInstalled, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        string? backup = null;
        var newest = Files(paths.BackupsDirectory).Select(f => f.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        if (_time.GetUtcNow().UtcDateTime - newest >= DailyBackup)
        {
            backup = await catalog.BackupAsync(paths.BackupsDirectory, "daily", ct).ConfigureAwait(false);
        }

        return new MaintenanceReport(backup, RotateBackups(), TrimThumbnails(ThumbnailCacheLimit), CleanTemp(),
            gpuInstalled && gpuDownloads is not null ? CleanFolder(gpuDownloads) : 0);
    }

    /// <summary>Backup at close, then rotation.</summary>
    public async Task<string> BackupAtCloseAsync(ICatalog catalog, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var backup = await catalog.BackupAsync(paths.BackupsDirectory, "close", ct).ConfigureAwait(false);
        RotateBackups();
        return backup;
    }

    /// <summary>Backup requested by the user (board 5h), then rotation.</summary>
    public Task<string> BackupNowAsync(ICatalog catalog, CancellationToken ct) => BackupAsync(catalog, "manual", ct);

    /// <summary>Backup before an operation the user cannot undo otherwise (the reset of the people, P22), then rotation.</summary>
    public async Task<string> BackupAsync(ICatalog catalog, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var backup = await catalog.BackupAsync(paths.BackupsDirectory, label, ct).ConfigureAwait(false);
        RotateBackups();
        return backup;
    }

    public int RotateBackups(int keep = KeepBackups)
    {
        var removed = 0;
        var kept = protectedBackup?.Invoke() is { } path ? Path.GetFullPath(path) : null;
        foreach (var old in Files(paths.BackupsDirectory).OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).Skip(keep)
                     .Where(f => !string.Equals(f.FullName, kept, StringComparison.OrdinalIgnoreCase)))
        {
            removed += TryDelete(old.FullName);
        }

        return removed;
    }

    /// <summary>Removes the oldest thumbnails until the cache is under 90 % of the limit.</summary>
    public int TrimThumbnails(long limit)
    {
        var files = Files(paths.ThumbnailsDirectory, recursive: true).OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = files.Sum(f => f.Length);
        if (total <= limit)
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in files)
        {
            if (total <= limit * 9 / 10)
            {
                break;
            }

            if (TryDelete(file.FullName) == 1)
            {
                total -= file.Length;
                removed++;
            }
        }

        return removed;
    }

    public int CleanTemp()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - TempMaxAge;
        return Files(paths.TempDirectory, recursive: true).Where(f => f.LastWriteTimeUtc < cutoff).Sum(f => TryDelete(f.FullName));
    }

    private int CleanFolder(string folder) => Files(folder, recursive: true).Sum(f => TryDelete(f.FullName));

    private int TryDelete(string path)
    {
        if (!cleaner.IsInternal(path))
        {
            return 0;
        }

        try
        {
            cleaner.DeleteInternalFile(path);
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0; // in use: next time
        }
    }

    private static IEnumerable<FileInfo> Files(string folder, bool recursive = false) =>
        Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles("*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            : [];
}
