// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PhotoFlow.App.Localization;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public partial class ExportView : UserControl
{
    public ExportView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExportViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.Get("Export.ChooseFolder"), AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            vm.SetFolder(path);
        }
    }

    private void OnFolderRadio(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ExportViewModel vm)
        {
            vm.NextToOriginals = false;
            if (string.IsNullOrEmpty(vm.Folder))
            {
                OnBrowse(sender, e);
            }
        }
    }
}
