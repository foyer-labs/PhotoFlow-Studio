// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class BrowseView : UserControl
{
    private BrowseViewModel? _viewModel;

    public BrowseView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
        RowsList.SizeChanged += (_, e) => UpdateColumns(e.NewSize.Width);
        QueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        QueryBox.SelectionChanged += OnSuggestionChosen;
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.FocusSearchRequested -= OnFocusSearch;
            }

            _viewModel = DataContext as BrowseViewModel;
            if (_viewModel is not null)
            {
                _viewModel.FocusSearchRequested += OnFocusSearch;
                UpdateColumns(RowsList.Bounds.Width);
            }
        };
    }

    private void UpdateColumns(double width)
    {
        if (_viewModel is not null && width > 0)
        {
            var stride = BrowseViewModel.CellSize + BrowseViewModel.CellGap;
            _viewModel.Columns = Math.Max(1, (int)Math.Floor((width - RowsList.Padding.Left - RowsList.Padding.Right) / stride));
        }
    }

    /// <summary>Enter with the suggestions closed: the text becomes free text (with a suggestion open, the box picks it).</summary>
    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.Key == Key.Enter && !QueryBox.IsDropDownOpen)
        {
            _viewModel.SubmitCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !QueryBox.IsDropDownOpen)
        {
            Focus();
            e.Handled = true;
        }
    }

    private void OnSuggestionChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && QueryBox.SelectedItem is SearchSuggestion suggestion)
        {
            Dispatcher.UIThread.Post(() =>
            {
                QueryBox.SelectedItem = null;
                QueryBox.Text = string.Empty;
                _ = _viewModel.AddTokenAsync(suggestion);
            });
        }
    }

    private void OnFocusSearch(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => QueryBox.Focus());

    /// <summary>A tag of the facets: the mode of the switch, the other one with Ctrl.</summary>
    private async void OnFacetTagPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: FacetItemViewModel item } && DataContext is BrowseViewModel browse && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            await browse.Facets.ClickTagAsync(item, e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta));
        }
    }

    private void OnCellPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: BrowseItemViewModel item } && _viewModel is not null)
        {
            _viewModel.Click(item);
            Focus();
            if (e.ClickCount == 2)
            {
                _viewModel.TogglePreviewCommand.Execute(null);
            }
        }
    }
}
