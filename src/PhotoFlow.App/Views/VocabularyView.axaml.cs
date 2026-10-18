// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class VocabularyView : UserControl
{
    private VocabularyViewModel? _viewModel;

    public VocabularyView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.EditRequested -= OnEditRequested;
            }

            _viewModel = DataContext as VocabularyViewModel;
            if (_viewModel is not null)
            {
                _viewModel.EditRequested += OnEditRequested;
                _viewModel.PropertyChanged += (_, e) =>
                {
                    // Editing ended (Enter or Esc): the keys go back to the page.
                    if (e.PropertyName == nameof(VocabularyViewModel.IsEditing) && _viewModel is { IsEditing: false } && EditBox.IsFocused)
                    {
                        Dispatcher.UIThread.Post(() => Focus());
                    }
                };
            }
        };
    }

    /// <summary>F2: the label field takes the focus; Enter approves (page key), Esc cancels.</summary>
    private void OnEditRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        EditBox.Focus();
        EditBox.SelectAll();
    });

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: VocabularyRow row } && _viewModel is not null)
        {
            _viewModel.Click(row);
            Focus();
        }
    }
}
