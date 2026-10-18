// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class PersonPageView : UserControl
{
    private PersonPageViewModel? _viewModel;
    private bool _wasEditing;

    public PersonPageView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelChanged;
            }

            _viewModel = DataContext as PersonPageViewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    /// <summary>F2 moves the focus into the name field, Ctrl M opens the list of the people; Enter or Esc gives it back to the page.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.PropertyName == nameof(PersonPageViewModel.IsMoving))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_viewModel.IsMoving)
                {
                    MoveBox.Focus();
                    MoveBox.IsDropDownOpen = true;
                }
                else
                {
                    Focus();
                }
            });
            return;
        }

        if (e.PropertyName == nameof(PersonPageViewModel.IsNewPerson) && _viewModel.IsNewPerson)
        {
            Dispatcher.UIThread.Post(() => NewPersonBox.Focus());
            return;
        }

        // Only when the rename starts or ends: the property is raised at every letter typed, and focusing the field
        // again selected its text, so the next letter replaced the name (one letter at most).
        if (e.PropertyName != nameof(PersonPageViewModel.IsEditing) || _viewModel.IsEditing == _wasEditing)
        {
            return;
        }

        _wasEditing = _viewModel.IsEditing;

        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel.IsEditing)
            {
                EditBox.Focus();
            }
            else
            {
                Focus();
            }
        });
    }

    private void OnFacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: FaceTileViewModel tile } && _viewModel is not null)
        {
            _viewModel.Click(tile, e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            Focus();
            if (e.ClickCount == 2)
            {
                _viewModel.OpenPhotoCommand.Execute(null); // double click: the photo the face comes from
            }
        }
    }

    /// <summary>A double click on the photo goes back to the faces.</summary>
    private void OnPhotoPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2 && _viewModel is { IsShowingPhoto: true })
        {
            e.Handled = true;
            _viewModel.ClosePhotoCommand.Execute(null);
            Focus();
        }
    }
}
