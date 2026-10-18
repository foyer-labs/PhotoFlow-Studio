// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.App.ViewModels;

/// <summary>
/// Startup with a damaged catalog (spec §9: integrity checked at opening, restore proposed from the last backup).
/// Restoring moves the damaged catalog next to the backups (never deleted) and starts again.
/// </summary>
public sealed partial class CatalogProblemViewModel(string error, CatalogRestoreService restore, Func<Task> retry) : PageViewModel
{
    private readonly CatalogBackup? _latest = restore.ListBackups().FirstOrDefault();

    public override string Title => Strings.Get("CatalogProblem.Title");

    public string Error { get; } = error;

    public bool HasBackup => _latest is not null;

    public string BackupText => _latest is { } backup
        ? Strings.Format("CatalogProblem.Backup", backup.CreatedAt.ToString("g", CultureInfo.CurrentCulture), Path.GetFileName(backup.Path))
        : Strings.Get("CatalogProblem.NoBackup");

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (_latest is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var replaced = await Task.Run(() => restore.RestoreNowAsync(_latest.Path, CancellationToken.None));
            Message = Strings.Format("CatalogProblem.Restored", replaced);
            await retry();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CatalogLockedException)
        {
            Message = Strings.Format("CatalogProblem.Failed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
