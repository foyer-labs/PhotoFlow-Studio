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

        // The keys go back to the page when the caption or tag field closes (Enter, Esc).
        foreach (var field in new Control[] { CaptionBox, TagBox })
        {
            field.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && e.NewValue is false)
                {
                    Focus();
                }
            };
        }
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.FocusSearchRequested -= OnFocusSearch;
                _viewModel.FocusTagRequested -= OnFocusTag;
                _viewModel.FocusCaptionRequested -= OnFocusCaption;
            }

            _viewModel = DataContext as BrowseViewModel;
            if (_viewModel is not null)
            {
                _viewModel.FocusSearchRequested += OnFocusSearch;
                _viewModel.FocusTagRequested += OnFocusTag;
                _viewModel.FocusCaptionRequested += OnFocusCaption;
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

    private void OnFocusTag(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => TagBox.Focus());

    private void OnFocusCaption(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        CaptionBox.Focus();
        CaptionBox.CaretIndex = CaptionBox.Text?.Length ?? 0;
    });

    /// <summary>A tag of the facets: the mode of the switch, the other one with Ctrl.</summary>
    private async void OnFacetTagPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: FacetItemViewModel item } && DataContext is BrowseViewModel browse && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            await browse.Facets.ClickTagAsync(item, e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta));
        }
    }

    /// <summary>A face clicked on the large preview: who is it?</summary>
    private async void OnFaceClicked(object? sender, FaceBoxViewModel face)
    {
        if (_viewModel is { IsLargePreview: true })
        {
            await _viewModel.IdentifyFaceAsync(face);
            FacePersonBox.Focus();
            FacePersonBox.IsDropDownOpen = true;
        }
    }

    /// <summary>A box drawn on the large preview: the face to add, then its person.</summary>
    private void OnFaceDrawn(object? sender, PhotoFlow.Core.Analysis.BoundingBox box)
    {
        _viewModel?.FaceDrawnOnPhoto(box);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            FacePersonBox.Focus();
            FacePersonBox.IsDropDownOpen = true;
        });
    }

    /// <summary>A double click on the large preview goes back to the grid, as one on a thumbnail opened it (not while drawing a face).</summary>
    private void OnLargePreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2 && _viewModel is { IsLargePreview: true, IsDrawingFace: false })
        {
            e.Handled = true;
            _viewModel.TogglePreviewCommand.Execute(null);
            Focus();
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
