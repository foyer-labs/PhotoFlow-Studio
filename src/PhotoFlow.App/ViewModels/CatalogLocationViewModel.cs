// SPDX-License-Identifier: Apache-2.0

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;

namespace PhotoFlow.App.ViewModels;

/// <summary>
/// First start (spec §9): where the catalog will be saved, asked before it is created. The local data folder is
/// proposed; another folder (a second disk, a network share) can be chosen. A PhotoFlow catalog already in the chosen
/// folder is used as it is. Then the application starts.
/// </summary>
public sealed partial class CatalogLocationViewModel(CatalogLocationService location, Func<Task> start) : PageViewModel
{
    public override string Title => Strings.Get("CatalogLocation.Title");

    /// <summary>The folder chosen so far (the proposal at first).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefault))]
    private string _folder = location.DefaultFolder;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    public bool IsDefault => string.Equals(Folder, location.DefaultFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Raised to show the folder picker (handled by the view).</summary>
    public event EventHandler? PickFolderRequested;

    [RelayCommand]
    private void ChooseFolder() => PickFolderRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void UseDefault() => Folder = location.DefaultFolder;

    public void UseFolder(string folder)
    {
        Folder = folder;
        Message = null;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        IsBusy = true;
        try
        {
            var existing = await Task.Run(() => location.ChooseAsync(Folder, CancellationToken.None));
            Message = Strings.Get(existing ? "CatalogLocation.Existing" : "CatalogLocation.Creating");
            await start();
        }
        catch (InvalidOperationException e)
        {
            Message = Strings.Format("CatalogLocation.Failed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
