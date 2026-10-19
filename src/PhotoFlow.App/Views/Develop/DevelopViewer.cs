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
    public static readonly StyledProperty<bool> CroppingProperty = AvaloniaProperty.Register<DevelopViewer, bool>(nameof(Cropping));
    public static readonly StyledProperty<Rect> CropRectProperty = AvaloniaProperty.Register<DevelopViewer, Rect>(nameof(CropRect), new Rect(0, 0, 1, 1));
    public static readonly StyledProperty<double> CropAspectProperty = AvaloniaProperty.Register<DevelopViewer, double>(nameof(CropAspect));

    private const double Handle = 10;
    private double _split = 0.5;
    private CropDrag _cropDrag;
    private Point _cropStart;
    private Rect _cropAtStart;
    private bool _draggingSplit;
    private Point? _panStart;
    private Vector _pan;

    static DevelopViewer()
    {
        AffectsRender<DevelopViewer>(BeforeProperty, AfterProperty, DifferenceProperty, RegionProperty, ActualSizeProperty, ModeProperty,
            ShowOriginalProperty, BeforeLabelProperty, AfterLabelProperty, CroppingProperty, CropRectProperty);
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

    /// <summary>Crop mode (M1): the whole straightened frame with the crop drawn on it.</summary>
    public bool Cropping { get => GetValue(CroppingProperty); set => SetValue(CroppingProperty, value); }

    /// <summary>The crop in fractions of the frame.</summary>
    public Rect CropRect { get => GetValue(CropRectProperty); set => SetValue(CropRectProperty, value); }

    /// <summary>Width / height in pixels the crop keeps while resized (0 = free).</summary>
    public double CropAspect { get => GetValue(CropAspectProperty); set => SetValue(CropAspectProperty, value); }

    /// <summary>The crop was moved or resized (fractions of the frame), at the end of the drag.</summary>
    public event EventHandler<Rect>? CropEdited;

    /// <summary>Double click in crop mode: the crop is done.</summary>
    public event EventHandler? CropConfirmed;

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
        if (Cropping)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                context.DrawImage(ShowOriginal ? before : After ?? before, rect);
            }

            DrawCrop(context, rect);
            return;
        }

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

    // ------------------------------------------------------------------ crop (M1)

    private enum CropDrag
    {
        None,
        Move,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    /// <summary>The crop in control coordinates.</summary>
    private Rect CropOnScreen(Rect image) =>
        new(image.X + CropRect.X * image.Width, image.Y + CropRect.Y * image.Height, CropRect.Width * image.Width, CropRect.Height * image.Height);

    private void DrawCrop(DrawingContext context, Rect image)
    {
        var crop = CropOnScreen(image);
        var shade = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        context.FillRectangle(shade, new Rect(image.X, image.Y, image.Width, Math.Max(0, crop.Y - image.Y)));
        context.FillRectangle(shade, new Rect(image.X, crop.Bottom, image.Width, Math.Max(0, image.Bottom - crop.Bottom)));
        context.FillRectangle(shade, new Rect(image.X, crop.Y, Math.Max(0, crop.X - image.X), crop.Height));
        context.FillRectangle(shade, new Rect(crop.Right, crop.Y, Math.Max(0, image.Right - crop.Right), crop.Height));
        var thirds = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1);
        for (var i = 1; i < 3; i++)
        {
            var x = crop.X + crop.Width * i / 3;
            var y = crop.Y + crop.Height * i / 3;
            context.DrawLine(thirds, new Point(x, crop.Y), new Point(x, crop.Bottom));
            context.DrawLine(thirds, new Point(crop.X, y), new Point(crop.Right, y));
        }

        context.DrawRectangle(null, new Pen(Brushes.White, 1.5), crop);
        var handle = new Pen(Brushes.White, 3);
        foreach (var (p, dx, dy) in new[] { (crop.TopLeft, 1, 1), (crop.TopRight, -1, 1), (crop.BottomLeft, 1, -1), (crop.BottomRight, -1, -1) })
        {
            context.DrawLine(handle, p, new Point(p.X + dx * 16, p.Y));
            context.DrawLine(handle, p, new Point(p.X, p.Y + dy * 16));
        }
    }

    private CropDrag HitCrop(Point p)
    {
        var crop = CropOnScreen(ImageRect());
        var left = Math.Abs(p.X - crop.Left) < Handle;
        var right = Math.Abs(p.X - crop.Right) < Handle;
        var top = Math.Abs(p.Y - crop.Top) < Handle;
        var bottom = Math.Abs(p.Y - crop.Bottom) < Handle;
        var insideX = p.X > crop.Left - Handle && p.X < crop.Right + Handle;
        var insideY = p.Y > crop.Top - Handle && p.Y < crop.Bottom + Handle;
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => CropDrag.TopLeft,
            (_, true, true, _) => CropDrag.TopRight,
            (true, _, _, true) => CropDrag.BottomLeft,
            (_, true, _, true) => CropDrag.BottomRight,
            (true, _, _, _) when insideY => CropDrag.Left,
            (_, true, _, _) when insideY => CropDrag.Right,
            (_, _, true, _) when insideX => CropDrag.Top,
            (_, _, _, true) when insideX => CropDrag.Bottom,
            _ => crop.Contains(p) ? CropDrag.Move : CropDrag.None,
        };
    }

    private static Cursor CursorFor(CropDrag drag) => drag switch
    {
        CropDrag.Move => new Cursor(StandardCursorType.SizeAll),
        CropDrag.Left or CropDrag.Right => new Cursor(StandardCursorType.SizeWestEast),
        CropDrag.Top or CropDrag.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
        CropDrag.TopLeft or CropDrag.BottomRight => new Cursor(StandardCursorType.TopLeftCorner),
        CropDrag.TopRight or CropDrag.BottomLeft => new Cursor(StandardCursorType.TopRightCorner),
        _ => Cursor.Default,
    };

    /// <summary>The crop after dragging by (<paramref name="dx"/>, <paramref name="dy"/>) fractions of the frame.</summary>
    private Rect Dragged(Rect r, double dx, double dy)
    {
        const double min = 0.03;
        if (_cropDrag == CropDrag.Move)
        {
            return new Rect(Math.Clamp(r.X + dx, 0, 1 - r.Width), Math.Clamp(r.Y + dy, 0, 1 - r.Height), r.Width, r.Height);
        }

        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        if (_cropDrag is CropDrag.Left or CropDrag.TopLeft or CropDrag.BottomLeft)
        {
            left = Math.Clamp(left + dx, 0, right - min);
        }

        if (_cropDrag is CropDrag.Right or CropDrag.TopRight or CropDrag.BottomRight)
        {
            right = Math.Clamp(right + dx, left + min, 1);
        }

        if (_cropDrag is CropDrag.Top or CropDrag.TopLeft or CropDrag.TopRight)
        {
            top = Math.Clamp(top + dy, 0, bottom - min);
        }

        if (_cropDrag is CropDrag.Bottom or CropDrag.BottomLeft or CropDrag.BottomRight)
        {
            bottom = Math.Clamp(bottom + dy, top + min, 1);
        }

        var result = new Rect(left, top, right - left, bottom - top);
        return CropAspect > 0 && Before is { } bitmap ? KeepAspect(result, bitmap.PixelSize) : result;
    }

    /// <summary>Keeps <see cref="CropAspect"/>: the dragged side decides, the other follows (anchored on the opposite edge).</summary>
    private Rect KeepAspect(Rect r, PixelSize frame)
    {
        var q = CropAspect * frame.Height / Math.Max(1, frame.Width); // width / height in fractions of the frame
        var widthLeads = _cropDrag is CropDrag.Left or CropDrag.Right
            || (_cropDrag is not (CropDrag.Top or CropDrag.Bottom) && r.Width / q >= r.Height);
        var w = widthLeads ? r.Width : r.Height * q;
        var h = widthLeads ? r.Width / q : r.Height;
        var anchorRight = _cropDrag is CropDrag.Left or CropDrag.TopLeft or CropDrag.BottomLeft;
        var anchorBottom = _cropDrag is CropDrag.Top or CropDrag.TopLeft or CropDrag.TopRight;
        var x = anchorRight ? r.Right - w : r.X;
        var y = anchorBottom ? r.Bottom - h : _cropDrag is CropDrag.Left or CropDrag.Right ? r.Y + (r.Height - h) / 2 : r.Y;
        if (_cropDrag is CropDrag.Top or CropDrag.Bottom)
        {
            x = r.X + (r.Width - w) / 2;
        }

        // Inside the frame, scaling down when needed.
        var scale = Math.Min(1, Math.Min(w > 0 ? Math.Min(1, (anchorRight ? r.Right : 1 - x) / w) : 1, h > 0 ? (anchorBottom ? r.Bottom : 1 - y) / h : 1));
        if (scale < 1)
        {
            w *= scale;
            h *= scale;
            x = anchorRight ? r.Right - w : x;
            y = anchorBottom ? r.Bottom - h : y;
        }

        return new Rect(Math.Clamp(x, 0, 1 - w), Math.Clamp(y, 0, 1 - h), w, h);
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

        if (Cropping)
        {
            if (e.ClickCount == 2)
            {
                CropConfirmed?.Invoke(this, EventArgs.Empty);
                return;
            }

            _cropDrag = HitCrop(p);
            if (_cropDrag != CropDrag.None)
            {
                _cropStart = p;
                _cropAtStart = CropRect;
                e.Pointer.Capture(this);
            }

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
        if (Cropping)
        {
            if (_cropDrag != CropDrag.None)
            {
                var image = ImageRect();
                CropRect = Dragged(_cropAtStart, (p.X - _cropStart.X) / Math.Max(1, image.Width), (p.Y - _cropStart.Y) / Math.Max(1, image.Height));
            }
            else
            {
                Cursor = CursorFor(HitCrop(p));
            }

            return;
        }

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
        if (_cropDrag != CropDrag.None)
        {
            _cropDrag = CropDrag.None;
            e.Pointer.Capture(null);
            if (CropRect != _cropAtStart)
            {
                CropEdited?.Invoke(this, CropRect);
            }

            return;
        }

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
