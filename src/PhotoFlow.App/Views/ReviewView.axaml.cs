// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class ReviewView : UserControl
{
    private ReviewViewModel? _viewModel;
    private bool _syncing;

    public ReviewView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
        RowsList.SizeChanged += (_, e) => UpdateColumns(e.NewSize.Width);
        CompareList.AddHandler(ScrollViewer.ScrollChangedEvent, OnCompareScrolled);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.RowFocusRequested -= OnRowFocusRequested;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as ReviewViewModel;
        if (_viewModel is not null)
        {
            _viewModel.RowFocusRequested += OnRowFocusRequested;
            _viewModel.PropertyChanged += OnViewModelChanged;
            UpdateColumns(RowsList.Bounds.Width);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ReviewViewModel.CellSize):
                UpdateColumns(RowsList.Bounds.Width);
                break;
            case nameof(ReviewViewModel.Zoom):
                ApplyZoom();
                break;
            case nameof(ReviewViewModel.Pan):
                ApplyPan();
                break;
        }
    }

    private void UpdateColumns(double width)
    {
        if (_viewModel is not null && width > 0)
        {
            var stride = _viewModel.CellSize + ReviewViewModel.CellGap;
            _viewModel.SetColumns((int)Math.Floor((width - RowsList.Padding.Left - RowsList.Padding.Right) / stride));
        }
    }

    private void OnRowFocusRequested(object? sender, ReviewRow row) => RowsList.ScrollIntoView(row);

    /// <summary>Click selects; Shift+click adds or removes the photo from the comparison (2b); a double click opens it large (P21).</summary>
    private void OnCellPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: ReviewItemViewModel item } || _viewModel is null)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _viewModel.ToggleCompared(item);
        }
        else
        {
            _viewModel.SelectCommand.Execute(item);
            if (e.ClickCount == 2)
            {
                _viewModel.ToggleLargePhotoCommand.Execute(null);
            }
        }

        Focus();
    }

    private IEnumerable<ScrollViewer> CompareViewers() =>
        CompareList.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.Classes.Contains("compare"));

    /// <summary>Z: one preview pixel per screen pixel, centred; F: fit.</summary>
    private void ApplyZoom()
    {
        foreach (var viewer in CompareViewers())
        {
            if (viewer.Content is Image image)
            {
                image.Stretch = _viewModel?.Zoom is null ? Stretch.Uniform : Stretch.None;
            }
        }

        Dispatcher();
        void Dispatcher() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var viewer in CompareViewers())
            {
                viewer.Offset = new Vector(Math.Max(0, (viewer.Extent.Width - viewer.Viewport.Width) / 2), Math.Max(0, (viewer.Extent.Height - viewer.Viewport.Height) / 2));
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Keyboard panning (↑ ↓ in the comparison): every view when synchronized, the focused one otherwise.</summary>
    private void ApplyPan()
    {
        if (_viewModel is null)
        {
            return;
        }

        foreach (var viewer in CompareViewers())
        {
            if (_viewModel.IsZoomSynced || (viewer.Tag as CompareItemViewModel)?.IsFocused == true)
            {
                viewer.Offset = new Vector(viewer.Offset.X, Math.Max(0, (viewer.Extent.Height - viewer.Viewport.Height) / 2) + _viewModel.Pan.Y);
            }
        }
    }

    /// <summary>Synchronized zoom (S): scrolling one view moves the others to the same place.</summary>
    private void OnCompareScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncing || _viewModel is not { IsZoomSynced: true } || e.Source is not ScrollViewer source || !source.Classes.Contains("compare"))
        {
            return;
        }

        _syncing = true;
        try
        {
            foreach (var viewer in CompareViewers().Where(v => v != source))
            {
                viewer.Offset = source.Offset;
            }
        }
        finally
        {
            _syncing = false;
        }
    }
}
