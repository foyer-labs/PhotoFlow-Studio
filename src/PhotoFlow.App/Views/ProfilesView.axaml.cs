// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class ProfilesView : UserControl
{
    private ProfilesViewModel? _viewModel;

    public ProfilesView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.RenameRequested -= OnRenameRequested;
            }

            _viewModel = DataContext as ProfilesViewModel;
            if (_viewModel is not null)
            {
                _viewModel.RenameRequested += OnRenameRequested;
            }
        };
    }

    /// <summary>F2: the name field takes the focus with the name selected.</summary>
    private void OnRenameRequested(object? sender, EventArgs e)
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }
}
