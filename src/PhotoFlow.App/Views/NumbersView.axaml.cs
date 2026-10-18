// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PhotoFlow.App.Localization;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class NumbersView : UserControl
{
    private NumbersViewModel? _viewModel;

    public NumbersView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.ImportRequested -= OnImportRequested;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as NumbersViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ImportRequested += OnImportRequested;
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    /// <summary>The text box of the list takes the keys while editing; the page gets them back afterwards.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NumbersViewModel.IsEditing) && _viewModel is { } viewModel)
        {
            if (viewModel.IsEditing)
            {
                EditBox.Focus();
            }
            else
            {
                Focus();
            }
        }
    }

    private void OnGroupPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: NumberGroupViewModel group } && _viewModel is not null)
        {
            _viewModel.SelectGroupCommand.Execute(group);
            Focus();
        }
    }

    private void OnPhotoPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: NumberPhotoViewModel photo } && _viewModel is not null)
        {
            _viewModel.SelectPhotoCommand.Execute(photo);
            Focus();
        }
    }

    private async void OnImportRequested(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || _viewModel is null)
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
            await _viewModel.ImportFileAsync(path);
        }

        Focus();
    }
}
