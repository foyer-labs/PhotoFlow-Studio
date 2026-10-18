// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Input;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class DuplicatesView : UserControl
{
    public DuplicatesView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
    }

    /// <summary>A click on a copy chooses it as the one to keep, like the keys 1-4.</summary>
    private void OnCopyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: DuplicateCopyViewModel copy } && DataContext is DuplicatesViewModel viewModel)
        {
            viewModel.ChooseCommand.Execute(copy.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Focus();
        }
    }

    private void OnGroupPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: DuplicateGroupItem item } && DataContext is DuplicatesViewModel viewModel)
        {
            viewModel.Click(item);
            Focus();
        }
    }
}
