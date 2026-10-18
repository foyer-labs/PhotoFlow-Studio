// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class PeopleView : UserControl
{
    private PeopleViewModel? _viewModel;

    public PeopleView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelChanged;
            }

            _viewModel = DataContext as PeopleViewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    private bool _wasRenaming;

    /// <summary>F2 moves the focus into the name field; Enter or Esc gives it back to the page.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only when renaming starts or ends: the property is raised at every letter typed, and focusing the field
        // again selected its text, so the next letter replaced the name (one letter at most).
        if (e.PropertyName != nameof(PeopleViewModel.IsRenaming) || _viewModel is null || _viewModel.IsRenaming == _wasRenaming)
        {
            return;
        }

        _wasRenaming = _viewModel.IsRenaming;
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel.IsRenaming)
            {
                RenameBox.Focus();
            }
            else
            {
                Focus();
            }
        });
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: PersonCardViewModel person } && _viewModel is not null)
        {
            _viewModel.Click(person, e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta));
            Focus();
        }
    }

    private void OnCardDoubleTapped(object? sender, TappedEventArgs e) => _viewModel?.OpenCommand.Execute(null);
}
