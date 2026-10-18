// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PhotoFlow.App.Localization;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class CatalogLocationView : UserControl
{
    private CatalogLocationViewModel? _viewModel;

    public CatalogLocationView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PickFolderRequested -= OnPickFolderRequested;
        }

        _viewModel = DataContext as CatalogLocationViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PickFolderRequested += OnPickFolderRequested;
        }

        base.OnDataContextChanged(e);
    }

    private async void OnPickFolderRequested(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || _viewModel is null)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.Get("CatalogLocation.Choose"), AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folder)
        {
            _viewModel.UseFolder(folder);
        }
    }
}
