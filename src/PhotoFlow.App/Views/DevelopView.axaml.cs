// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services.Develop;
using Avalonia.VisualTree;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public partial class DevelopView : UserControl
{
    private DevelopViewModel? _vm;

    public DevelopView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
        Viewer.Picked += (_, point) => _ = _vm?.PickAtAsync(point);
        Viewer.ZoomRequested += (_, point) => _vm?.ZoomAt(point);
        Viewer.PanRequested += (_, centre) => _vm?.PanTo(centre);
        Viewer.CropEdited += (_, rect) => _vm?.SetCrop(rect);
        Viewer.CropConfirmed += (_, _) => _vm?.CropDoneCommand.Execute(null);
        Viewer.SizeChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.Viewport = Viewer.ViewportPixels;
            }
        };
        Curve.CurveChanged += (_, points) => _vm?.PreviewCurve(points);
        Curve.CurveEdited += (_, points) =>
        {
            if (_vm is not null)
            {
                _vm.Curve = [.. points];
            }
        };
        // Double click on a slider: back to its recommended value (as in Denoiser).
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);
        // \ held: the original (decision S13); released, back to the result.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKey, RoutingStrategies.Tunnel);
        // Files and folders dropped from Explorer open without importing them (decision S24).
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.OpenFilesRequested -= OnOpenFiles;
        }

        _vm = DataContext as DevelopViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.OpenFilesRequested += OnOpenFiles;
            Curve.SetPoints(_vm.Curve);
        }
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DevelopViewModel.Curve) && _vm is not null && !_vm.Curve.SequenceEqual(Curve.Points))
        {
            Curve.SetPoints(_vm.Curve);
        }
        else if (e.PropertyName == nameof(DevelopViewModel.Region))
        {
            Viewer.ResetPan();
        }
    }

    private async void OnOpenFiles(object? sender, EventArgs e)
    {
        if (_vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var photos = new FilePickerFileType(Strings.Get("Develop.OpenFiles.Type")) { Patterns = [.. DevelopFiles.Extensions.Select(x => "*." + x)] };
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("Develop.OpenFiles.Title"),
            AllowMultiple = true,
            FileTypeFilter = [photos, FilePickerFileTypes.All],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
        {
            await vm.OpenFilesAsync(paths);
        }
    }

    private static IEnumerable<string> DroppedPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles() is { } items ? items.Select(i => i.TryGetLocalPath()).OfType<string>() : [];

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedPaths(e).Any() ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = DroppedPaths(e).ToList();
        e.Handled = true;
        if (_vm is { } vm && paths.Count > 0)
        {
            await vm.OpenFilesAsync(paths);
        }
    }

    private static bool IsBackslash(Key key) => key is Key.OemBackslash or Key.Oem5 or Key.OemPipe;

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null || !IsBackslash(e.Key) || e.Source is TextBox)
        {
            return;
        }

        _vm.ShowOriginal = e.RoutedEvent == KeyDownEvent;
        e.Handled = true;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual visual && visual.FindAncestorOfType<Slider>(includeSelf: true) is { Tag: string tag } slider
            && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            slider.Value = value;
            e.Handled = true;
        }
    }

    private void OnStripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is not null && sender is Control { DataContext: DevelopPhotoItem item })
        {
            var add = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _vm.Click(item, add);
            Viewer.Focus();
        }
    }
}
