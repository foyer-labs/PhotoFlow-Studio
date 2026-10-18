// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PhotoFlow.Core.Analysis;

namespace PhotoFlow.App.Views;

/// <summary>
/// A photo with its faces boxed and named (spec §7, 13 Oct 2026). While <see cref="IsDrawing"/> is on, a box dragged
/// with the mouse raises <see cref="FaceDrawn"/> with its normalized coordinates (0-1 of the photo).
/// </summary>
public sealed partial class FacesPhotoView : UserControl
{
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<FacesPhotoView, IImage?>(nameof(Source));

    public static readonly StyledProperty<double> ImageWidthProperty = AvaloniaProperty.Register<FacesPhotoView, double>(nameof(ImageWidth));

    public static readonly StyledProperty<double> ImageHeightProperty = AvaloniaProperty.Register<FacesPhotoView, double>(nameof(ImageHeight));

    public static readonly StyledProperty<IEnumerable?> FacesProperty = AvaloniaProperty.Register<FacesPhotoView, IEnumerable?>(nameof(Faces));

    public static readonly StyledProperty<bool> ShowFacesProperty = AvaloniaProperty.Register<FacesPhotoView, bool>(nameof(ShowFaces), true);

    public static readonly StyledProperty<bool> IsDrawingProperty = AvaloniaProperty.Register<FacesPhotoView, bool>(nameof(IsDrawing));

    private Point? _start;

    public FacesPhotoView()
    {
        InitializeComponent();
        Surface.PointerPressed += OnPressed;
        Surface.PointerMoved += OnMoved;
        Surface.PointerReleased += OnReleased;
    }

    /// <summary>A box drawn by the user, normalized to the photo.</summary>
    public event EventHandler<BoundingBox>? FaceDrawn;

    /// <summary>A face clicked (not while drawing): who is it?</summary>
    public event EventHandler<ViewModels.FaceBoxViewModel>? FaceClicked;

    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public double ImageWidth { get => GetValue(ImageWidthProperty); set => SetValue(ImageWidthProperty, value); }

    public double ImageHeight { get => GetValue(ImageHeightProperty); set => SetValue(ImageHeightProperty, value); }

    public IEnumerable? Faces { get => GetValue(FacesProperty); set => SetValue(FacesProperty, value); }

    public bool ShowFaces { get => GetValue(ShowFacesProperty); set => SetValue(ShowFacesProperty, value); }

    public bool IsDrawing { get => GetValue(IsDrawingProperty); set => SetValue(IsDrawingProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsDrawingProperty)
        {
            Surface.Cursor = IsDrawing ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
            if (!IsDrawing)
            {
                _start = null;
                Drawing.IsVisible = false;
            }
        }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsDrawing || ImageWidth <= 0 || ImageHeight <= 0 || !e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        _start = Clamp(e.GetPosition(Surface));
        e.Pointer.Capture(Surface);
        Show(_start.Value, _start.Value);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_start is { } start)
        {
            Show(start, Clamp(e.GetPosition(Surface)));
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_start is not { } start)
        {
            return;
        }

        e.Handled = true;
        e.Pointer.Capture(null);
        _start = null;
        var end = Clamp(e.GetPosition(Surface));
        var box = new BoundingBox(
            (float)(Math.Min(start.X, end.X) / ImageWidth), (float)(Math.Min(start.Y, end.Y) / ImageHeight),
            (float)(Math.Max(start.X, end.X) / ImageWidth), (float)(Math.Max(start.Y, end.Y) / ImageHeight));
        if (box.X2 - box.X1 >= Core.Catalog.FaceDefaults.MinManualSide && box.Y2 - box.Y1 >= Core.Catalog.FaceDefaults.MinManualSide)
        {
            FaceDrawn?.Invoke(this, box); // the box stays shown until drawing ends
        }
        else
        {
            Drawing.IsVisible = false;
        }
    }

    private void OnFacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsDrawing && sender is Control { Tag: ViewModels.FaceBoxViewModel face } && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
        {
            e.Handled = true;
            FaceClicked?.Invoke(this, face);
        }
    }

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, ImageWidth), Math.Clamp(p.Y, 0, ImageHeight));

    private void Show(Point a, Point b)
    {
        Canvas.SetLeft(Drawing, Math.Min(a.X, b.X));
        Canvas.SetTop(Drawing, Math.Min(a.Y, b.Y));
        Drawing.Width = Math.Abs(a.X - b.X);
        Drawing.Height = Math.Abs(a.Y - b.Y);
        Drawing.BorderThickness = new Thickness(Math.Max(2, Math.Max(ImageWidth, ImageHeight) / 300));
        Drawing.IsVisible = true;
    }
}
