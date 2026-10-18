// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PhotoFlow.App.Localization;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class ImportWizardView : UserControl
{
    public ImportWizardView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
        AddDestination.Click += async (_, _) =>
        {
            if (DataContext is ImportWizardViewModel viewModel
                && TopLevel.GetTopLevel(this)?.StorageProvider is { CanPickFolder: true } storage)
            {
                var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.Get("Import.AddDestination") });
                if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
                {
                    viewModel.AddDestination(path);
                }
            }
        };
        AttachedToVisualTree += (_, _) => EventName.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ImportWizardViewModel viewModel)
        {
            viewModel.ImportEntryListRequested -= OnImportEntryList;
            viewModel.ImportEntryListRequested += OnImportEntryList;
        }
    }

    private async void OnImportEntryList(object? sender, EventArgs e)
    {
        if (sender is not ImportWizardViewModel viewModel || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("Numbers.List.Import"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Strings.Get("Numbers.List.FileType")) { Patterns = ["*.csv", "*.txt", "*.xlsx"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await viewModel.ImportEntryListAsync(path);
        }
    }
}
