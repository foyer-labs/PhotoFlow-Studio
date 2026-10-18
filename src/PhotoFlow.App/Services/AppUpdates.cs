// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using PhotoFlow.Core.Paths;
using Velopack;
using Velopack.Sources;

namespace PhotoFlow.App.Services;

/// <summary>
/// Updates of the application (spec §11): Velopack, releases <c>vX.Y.Z</c> of the GitHub repository (the pre-releases
/// holding model files are never offered). Checked at startup in the background when the user allows it; installed
/// only after the user confirms and never while a job runs. When PhotoFlow runs from a build folder (not installed)
/// nothing is checked.
/// </summary>
public sealed class AppUpdates
{
    public const string Feed = "https://github.com/foyer-labs/PhotoFlow";

    private UpdateManager? _manager;
    private bool _created;
    private UpdateInfo? _available;

    /// <summary>Installed with the installer (updates possible), not run from a build folder.</summary>
    public bool IsInstalled => Manager?.IsInstalled ?? false;

    /// <summary>Made on first use; null where Velopack did not start the process (tests, build folder).</summary>
    private UpdateManager? Manager
    {
        get
        {
            if (!_created)
            {
                _created = true;
                try
                {
                    _manager = new UpdateManager(new GithubSource(Feed, null, prerelease: false));
                }
                catch (InvalidOperationException)
                {
                    _manager = null;
                }
            }

            return _manager;
        }
    }

    /// <summary>The version of this build ("0.1.0").</summary>
    public static string CurrentVersion =>
        typeof(AppUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>The newer version found by the last check, or null.</summary>
    public string? AvailableVersion => _available?.TargetFullRelease.Version.ToString();

    /// <summary>Looks for a newer release; returns its version, or null (none, or not installed).</summary>
    public async Task<string?> CheckAsync()
    {
        if (!IsInstalled || Manager is not { } manager)
        {
            return null;
        }

        _available = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return AvailableVersion;
    }

    /// <summary>Downloads the update found (progress 0-100).</summary>
    public Task DownloadAsync(Action<int>? progress, CancellationToken ct) =>
        _available is null || Manager is not { } manager ? Task.CompletedTask : manager.DownloadUpdatesAsync(_available, progress, ct);

    /// <summary>Closes PhotoFlow, installs the downloaded update and starts the new version.</summary>
    public void ApplyAndRestart()
    {
        if (_available is not null && Manager is { } manager)
        {
            manager.ApplyUpdatesAndRestart(_available.TargetFullRelease);
        }
    }
}

/// <summary>
/// At uninstall (spec §11, "Dati sul disco"): thumbnails, caches and temporary files go; configuration, catalog and
/// models stay. Only PhotoFlow's internal folders are emptied, through <see cref="InternalFileCleaner"/> (B1).
/// </summary>
public static class UninstallCleanup
{
    public static void Run(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var cleaner = new InternalFileCleaner(paths);
        foreach (var root in new[] { paths.CacheDirectory, paths.TempDirectory }.Where(Directory.Exists))
        {
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                Try(() => cleaner.DeleteInternalDirectory(folder));
            }

            foreach (var file in Directory.EnumerateFiles(root))
            {
                Try(() => cleaner.DeleteInternalFile(file));
            }
        }
    }

    private static void Try(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // a file in use or a link: left in place, the uninstall goes on
        }
    }
}
