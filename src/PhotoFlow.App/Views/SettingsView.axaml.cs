// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class SettingsView : UserControl
{
    private SettingsViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PickRequested -= OnPickRequested;
            _viewModel.ApplyTheme = null;
        }

        _viewModel = DataContext as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PickRequested += OnPickRequested;
            _viewModel.ApplyTheme = App.ApplyTheme;
        }
    }

    private async void OnPickRequested(object? sender, SettingsPick pick)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || _viewModel is null)
        {
            return;
        }

        if (pick == SettingsPick.ExportCatalog)
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.Get("Settings.Catalog.ExportButton"),
                SuggestedFileName = SettingsViewModel.ExportFileName,
                DefaultExtension = CatalogPackage.Extension,
                FileTypeChoices = [new FilePickerFileType(Strings.Get("Settings.Catalog.FileType")) { Patterns = ["*" + CatalogPackage.Extension] }],
            });
            if (file?.TryGetLocalPath() is { } path)
            {
                await _viewModel.ExportCatalogAsync(path);
            }
        }
        else if (pick == SettingsPick.ImportCatalog)
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Get("Settings.Catalog.ImportButton"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Strings.Get("Settings.Catalog.FileType")) { Patterns = ["*" + CatalogPackage.Extension] }],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                await _viewModel.LoadPackageAsync(path);
            }
        }
        else if (pick == SettingsPick.ImportRoot)
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.Get("Settings.Catalog.ChooseFolder") });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folder)
            {
                _viewModel.UseRoot(folder);
            }
        }
        else if (pick == SettingsPick.MoveCatalog)
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.Get("Settings.Catalog.Move"), AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folder)
            {
                _viewModel.MoveCatalogTo(folder);
            }
        }
        else if (pick == SettingsPick.OpenCatalog)
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Get("Settings.Catalog.OpenExisting"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Strings.Get("Settings.Catalog.CatalogFileType")) { Patterns = ["*.pfdb"] }],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } file)
            {
                await _viewModel.OpenCatalogAsync(file);
            }
        }
        else if (pick == SettingsPick.OfflinePackage)
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.Get("Settings.Models.ImportOffline") });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folder)
            {
                await _viewModel.ImportOfflinePackageAsync(folder);
            }
        }
        else if (pick == SettingsPick.Editor)
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Strings.Get("Settings.Editor.Choose"), AllowMultiple = false });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } file)
            {
                await _viewModel.UseEditorAsync(file);
            }
        }
        else
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Strings.Get("Settings.ExifTool.Other"), AllowMultiple = false });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } file)
            {
                await _viewModel.UseExifToolAsync(file);
            }
        }
    }
}
