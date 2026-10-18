// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PhotoFlow.App.Localization;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class LibrariesView : UserControl
{
    private LibrariesViewModel? _viewModel;

    public LibrariesView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.FolderPickRequested -= OnFolderPickRequested;
            _viewModel.CatalogPickRequested -= OnCatalogPickRequested;
        }

        _viewModel = DataContext as LibrariesViewModel;
        if (_viewModel is not null)
        {
            _viewModel.FolderPickRequested += OnFolderPickRequested;
            _viewModel.CatalogPickRequested += OnCatalogPickRequested;
        }
    }

    private async void OnCatalogPickRequested(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || _viewModel is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("Libraries.ImportCatalog"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Strings.Get("Settings.Catalog.FileType")) { Patterns = ["*" + Services.CatalogPackage.Extension] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            _viewModel.ImportCatalog(path);
        }
    }

    private async void OnFolderPickRequested(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage || _viewModel is null)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.Get("Libraries.AddFolder") });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            await _viewModel.AddLibraryAsync(path);
        }
    }
}
