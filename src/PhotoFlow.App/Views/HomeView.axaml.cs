// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PhotoFlow.App.Localization;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class HomeView : UserControl
{
    private HomeViewModel? _viewModel;

    public HomeView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
        BrowseLibrary.Click += async (_, _) =>
        {
            if (await PickFolderAsync(Strings.Get("FirstRun.Library")) is { } folder && _viewModel is not null)
            {
                _viewModel.FirstRun.LibraryRoot = folder;
            }
        };
        ImportOffline.Click += async (_, _) =>
        {
            if (await PickFolderAsync(Strings.Get("FirstRun.ImportOffline")) is { } folder && _viewModel is not null)
            {
                await _viewModel.FirstRun.ImportOfflinePackageAsync(folder);
            }
        };
        OpenLog.Click += (_, _) =>
        {
            if (_viewModel is not null)
            {
                Process.Start(new ProcessStartInfo(_viewModel.LogsDirectory) { UseShellExecute = true });
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.FolderPickRequested -= OnFolderPickRequested;
        }

        _viewModel = DataContext as HomeViewModel;
        if (_viewModel is not null)
        {
            _viewModel.FolderPickRequested += OnFolderPickRequested;
        }
    }

    private async void OnFolderPickRequested(object? sender, EventArgs e)
    {
        if (await PickFolderAsync(Strings.Get("Home.QuickAction.ImportFolder")) is { } folder)
        {
            _viewModel?.ImportFolder(folder);
        }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
