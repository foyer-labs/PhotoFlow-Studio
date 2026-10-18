// SPDX-License-Identifier: Apache-2.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PhotoFlow.App.Views.Develop;

/// <summary>How the viewer compares the original and the result (Y cycles them).</summary>
public enum CompareMode
{
    Split,
    After,
    Before,
    Difference,
}

/// <summary>
/// The photo in Develop (decisions S6, S8): before and after with a split line, «after only», «before only», «noise
/// removed». The bitmaps cover <see cref="Region"/> of the photo: the whole photo when fitted, a region at actual size
/// (one photo pixel per screen pixel) otherwise. Dragging at actual size pans; double click toggles fit / 100% on the
/// point; in eyedropper mode a click picks the point.
/// </summary>
public sealed class DevelopViewer : Control
{
    public static readonly StyledProperty<Bitmap?> BeforeProperty = AvaloniaProperty.Register<DevelopViewer, Bitmap?>(nameof(Before));
    public static readonly StyledProperty<Bitmap?> AfterProperty = AvaloniaProperty.Register<DevelopViewer, Bitmap?>(nameof(After));
    public static readonly StyledProperty<Bitmap?> DifferenceProperty = AvaloniaProperty.Register<DevelopViewer, Bitmap?>(nameof(Difference));
    public static readonly StyledProperty<Rect> RegionProperty = AvaloniaProperty.Register<DevelopViewer, Rect>(nameof(Region));
    public static readonly StyledProperty<bool> ActualSizeProperty = AvaloniaProperty.Register<DevelopViewer, bool>(nameof(ActualSize));
    public static readonly StyledProperty<CompareMode> ModeProperty = AvaloniaProperty.Register<DevelopViewer, CompareMode>(nameof(Mode));
    public static readonly StyledProperty<bool> ShowOriginalProperty = AvaloniaProperty.Register<DevelopViewer, bool>(nameof(ShowOriginal));
    public static readonly StyledProperty<bool> PickingProperty = AvaloniaProperty.Register<DevelopViewer, bool>(nameof(Picking));
    public static readonly StyledProperty<string?> BeforeLabelProperty = AvaloniaProperty.Register<DevelopViewer, string?>(nameof(BeforeLabel));
    public static readonly StyledProperty<string?> AfterLabelProperty = AvaloniaProperty.Register<DevelopViewer, string?>(nameof(AfterLabel));

    private double _split = 0.5;
    private bool _draggingSplit;
    private Point? _panStart;
    private Vector _pan;

    static DevelopViewer()
    {
        AffectsRender<DevelopViewer>(BeforeProperty, AfterProperty, DifferenceProperty, RegionProperty, ActualSizeProperty, ModeProperty,
            ShowOriginalProperty, BeforeLabelProperty, AfterLabelProperty);
        FocusableProperty.OverrideDefaultValue<DevelopViewer>(true);
        ClipToBoundsProperty.OverrideDefaultValue<DevelopViewer>(true);
    }

    public Bitmap? Before { get => GetValue(BeforeProperty); set => SetValue(BeforeProperty, value); }

    public Bitmap? After { get => GetValue(AfterProperty); set => SetValue(AfterProperty, value); }

    public Bitmap? Difference { get => GetValue(DifferenceProperty); set => SetValue(DifferenceProperty, value); }

    /// <summary>Part of the photo (in photo pixels) the bitmaps show.</summary>
    public Rect Region { get => GetValue(RegionProperty); set => SetValue(RegionProperty, value); }

    public bool ActualSize { get => GetValue(ActualSizeProperty); set => SetValue(ActualSizeProperty, value); }

    public CompareMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    /// <summary>\ held: the original everywhere.</summary>
    public bool ShowOriginal { get => GetValue(ShowOriginalProperty); set => SetValue(ShowOriginalProperty, value); }

    public bool Picking { get => GetValue(PickingProperty); set => SetValue(PickingProperty, value); }

    public string? BeforeLabel { get => GetValue(BeforeLabelProperty); set => SetValue(BeforeLabelProperty, value); }

    public string? AfterLabel { get => GetValue(AfterLabelProperty); set => SetValue(AfterLabelProperty, value); }

    /// <summary>A point of the photo was clicked with the eyedropper (photo coordinates).</summary>
    public event EventHandler<Point>? Picked;

    /// <summary>Double click: toggle fit / actual size centred on this point of the photo.</summary>
    public event EventHandler<Point>? ZoomRequested;

    /// <summary>At actual size the view was dragged: new centre of the photo to show.</summary>
    public event EventHandler<Point>? PanRequested;

    /// <summary>Size of the viewport in physical pixels (for the region to ask at actual size).</summary>
    public PixelSize ViewportPixels
    {
        get
        {
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            return new PixelSize(Math.Max(1, (int)(Bounds.Width * scale)), Math.Max(1, (int)(Bounds.Height * scale)));
        }
    }

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    /// <summary>Where the bitmaps are drawn, in control coordinates.</summary>
    private Rect ImageRect()
    {
        if (Before is not { } bitmap)
        {
            return default;
        }

        var size = bitmap.PixelSize;
        if (ActualSize)
        {
            var w = size.Width / Scaling;
            var h = size.Height / Scaling;
            return new Rect((Bounds.Width - w) / 2 + _pan.X, (Bounds.Height - h) / 2 + _pan.Y, w, h);
        }

        var fit = Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height);
        var fw = size.Width * fit;
        var fh = size.Height * fit;
        return new Rect((Bounds.Width - fw) / 2, (Bounds.Height - fh) / 2, fw, fh);
    }

    /// <summary>Control coordinates → photo coordinates.</summary>
    private Point ToPhoto(Point p)
    {
        var r = ImageRect();
        if (r.Width <= 0 || Region.Width <= 0)
        {
            return default;
        }

        return new Point(Region.X + (p.X - r.X) / r.Width * Region.Width, Region.Y + (p.Y - r.Y) / r.Height * Region.Height);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Resource("PF.Brush.Viewer", Brushes.Black), new Rect(Bounds.Size));
        if (Before is not { } before)
        {
            return;
        }

        var rect = ImageRect();
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = ActualSize ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
        {
            var after = After ?? before;
            var mode = ShowOriginal ? CompareMode.Before : Mode;
            switch (mode)
            {
                case CompareMode.Before:
                    context.DrawImage(before, rect);
                    break;
                case CompareMode.After:
                    context.DrawImage(after, rect);
                    break;
                case CompareMode.Difference:
                    context.DrawImage(Difference ?? after, rect);
                    break;
                default:
                    var x = rect.X + rect.Width * _split;
                    using (context.PushClip(new Rect(rect.X, rect.Y, Math.Max(0, x - rect.X), rect.Height)))
                    {
                        context.DrawImage(before, rect);
                    }

                    using (context.PushClip(new Rect(x, rect.Y, Math.Max(0, rect.Right - x), rect.Height)))
                    {
                        context.DrawImage(after, rect);
                    }

                    context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), 3), new Point(x, rect.Y), new Point(x, rect.Bottom));
                    context.DrawLine(new Pen(Brushes.White, 1), new Point(x, rect.Y), new Point(x, rect.Bottom));
                    break;
            }
        }

        DrawLabels(context, rect);
    }

    private void DrawLabels(DrawingContext context, Rect rect)
    {
        var mode = ShowOriginal ? CompareMode.Before : Mode;
        var typeface = new Typeface(Resource("PF.Font.Mono"), FontStyle.Normal, FontWeight.SemiBold);
        void Tag(string? text, double x, bool right)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, Brushes.White);
            var w = formatted.Width + 12;
            var left = right ? x - w - 8 : x + 8;
            left = Math.Clamp(left, 4, Math.Max(4, Bounds.Width - w - 4));
            var box = new Rect(left, Math.Max(4, rect.Y + 6), w, formatted.Height + 6);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null, box, 3, 3);
            context.DrawText(formatted, new Point(box.X + 6, box.Y + 3));
        }

        switch (mode)
        {
            case CompareMode.Split:
                var x = rect.X + rect.Width * _split;
                Tag(BeforeLabel, x, true);
                Tag(AfterLabel, x, false);
                break;
            case CompareMode.Before:
                Tag(BeforeLabel, rect.X, false);
                break;
            default:
                Tag(AfterLabel, rect.X, false);
                break;
        }
    }

    private IBrush Resource(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private FontFamily Resource(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is FontFamily family ? family : FontFamily.Default;

    /// <summary>Back to the centre (a new photo or a new region arrived).</summary>
    public void ResetPan()
    {
        _pan = default;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Before is null)
        {
            return;
        }

        if (Picking)
        {
            Picked?.Invoke(this, ToPhoto(p));
            return;
        }

        if (e.ClickCount == 2)
        {
            ZoomRequested?.Invoke(this, ToPhoto(p));
            return;
        }

        var rect = ImageRect();
        if (Mode == CompareMode.Split && Math.Abs(p.X - (rect.X + rect.Width * _split)) < 12)
        {
            _draggingSplit = true;
        }
        else if (ActualSize)
        {
            _panStart = p - _pan;
        }

        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_draggingSplit)
        {
            var rect = ImageRect();
            _split = Math.Clamp((p.X - rect.X) / Math.Max(rect.Width, 1), 0, 1);
            InvalidateVisual();
        }
        else if (_panStart is { } start)
        {
            _pan = p - start;
            InvalidateVisual();
        }
        else
        {
            var rect = ImageRect();
            Cursor = Picking ? new Cursor(StandardCursorType.Cross)
                : Mode == CompareMode.Split && Math.Abs(p.X - (rect.X + rect.Width * _split)) < 12 ? new Cursor(StandardCursorType.SizeWestEast)
                : ActualSize ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panStart is not null && _pan != default)
        {
            // The centre of the view in photo coordinates becomes the centre of the next region.
            var centre = ToPhoto(new Point(Bounds.Width / 2, Bounds.Height / 2));
            PanRequested?.Invoke(this, centre);
        }

        _draggingSplit = false;
        _panStart = null;
        e.Pointer.Capture(null);
    }
}
