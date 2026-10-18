// SPDX-License-Identifier: Apache-2.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PhotoFlow.App.Views.Develop;

/// <summary>
/// Tone curve over the RGB histogram (decision S7: the histogram is always visible, on top of the panel). Click on an
/// empty spot adds a point, drag moves it, double click (or dragging it out of the box) removes it; the end points move
/// only vertically. <see cref="CurveChanged"/> is raised while dragging, <see cref="CurveEdited"/> on release.
/// </summary>
public sealed class CurveEditor : Control
{
    public const int MaxPoints = 16;
    private const double Radius = 4.5;
    private const double Grab = 9;
    private const double MinGap = 0.02;
    /// <summary>Share of black or white pixels above which clipped shadows or highlights are signalled.</summary>
    private const double ClipWarning = 0.002;

    private List<Point> _points = [new(0, 0), new(1, 1)];
    private int? _drag;
    private bool _outside;

    public static readonly StyledProperty<int[][]?> HistogramProperty = AvaloniaProperty.Register<CurveEditor, int[][]?>(nameof(Histogram));

    static CurveEditor()
    {
        AffectsRender<CurveEditor>(HistogramProperty);
        FocusableProperty.OverrideDefaultValue<CurveEditor>(false);
    }

    public CurveEditor()
    {
        Height = 150;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>Red, green, blue, luminance × bins (null: no photo).</summary>
    public int[][]? Histogram
    {
        get => GetValue(HistogramProperty);
        set => SetValue(HistogramProperty, value);
    }

    public event EventHandler<IReadOnlyList<Point>>? CurveChanged;

    public event EventHandler<IReadOnlyList<Point>>? CurveEdited;

    public IReadOnlyList<Point> Points => _points;

    public void SetPoints(IEnumerable<Point> points)
    {
        var list = points.OrderBy(p => p.X).ToList();
        _points = list.Count >= 2 ? list : [new(0, 0), new(1, 1)];
        _drag = null;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ geometry

    private Rect Area => new(6, 12, Math.Max(1, Bounds.Width - 12), Math.Max(1, Bounds.Height - 18));

    private Point ToScreen(double x, double y)
    {
        var a = Area;
        return new Point(a.X + x * a.Width, a.Bottom - y * a.Height);
    }

    private Point ToCurve(Point p)
    {
        var a = Area;
        return new Point((p.X - a.X) / a.Width, (a.Bottom - p.Y) / a.Height);
    }

    private int? PointAt(Point screen)
    {
        int? best = null;
        var bestDistance = Grab;
        for (var i = 0; i < _points.Count; i++)
        {
            var d = Point.Distance(ToScreen(_points[i].X, _points[i].Y), screen);
            if (d <= bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }

        return best;
    }

    /// <summary>Monotone cubic interpolation (Fritsch–Carlson), the same curve the engine applies.</summary>
    public static double Evaluate(IReadOnlyList<Point> points, double x)
    {
        var n = points.Count;
        if (n < 2)
        {
            return x;
        }

        x = Math.Clamp(x, points[0].X, points[n - 1].X);
        if (n == 2)
        {
            var t0 = (x - points[0].X) / Math.Max(points[1].X - points[0].X, 1e-9);
            return points[0].Y + t0 * (points[1].Y - points[0].Y);
        }

        var h = new double[n - 1];
        var delta = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            h[i] = Math.Max(points[i + 1].X - points[i].X, 1e-9);
            delta[i] = (points[i + 1].Y - points[i].Y) / h[i];
        }

        var m = new double[n];
        m[0] = delta[0];
        m[n - 1] = delta[n - 2];
        for (var i = 1; i < n - 1; i++)
        {
            m[i] = delta[i - 1] * delta[i] > 0 ? (delta[i - 1] + delta[i]) / 2 : 0;
        }

        for (var k = 0; k < n - 1; k++)
        {
            if (delta[k] == 0)
            {
                m[k] = m[k + 1] = 0;
                continue;
            }

            var a = m[k] / delta[k];
            var b = m[k + 1] / delta[k];
            var s = a * a + b * b;
            if (s > 9)
            {
                var t = 3 / Math.Sqrt(s);
                m[k] = t * a * delta[k];
                m[k + 1] = t * b * delta[k];
            }
        }

        var j = 0;
        while (j < n - 2 && x > points[j + 1].X)
        {
            j++;
        }

        var tt = (x - points[j].X) / h[j];
        double t2 = tt * tt, t3 = t2 * tt;
        var y = (2 * t3 - 3 * t2 + 1) * points[j].Y + (t3 - 2 * t2 + tt) * h[j] * m[j]
                + (-2 * t3 + 3 * t2) * points[j + 1].Y + (t3 - t2) * h[j] * m[j + 1];
        return Math.Clamp(y, 0, 1);
    }

    // ------------------------------------------------------------------ drawing

    public override void Render(DrawingContext context)
    {
        var a = Area;
        context.FillRectangle(Resource("PF.Brush.Viewer", Brushes.Black), new Rect(Bounds.Size));
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
        for (var i = 1; i < 4; i++)
        {
            context.DrawLine(grid, new Point(a.X + a.Width * i / 4, a.Y), new Point(a.X + a.Width * i / 4, a.Bottom));
            context.DrawLine(grid, new Point(a.X, a.Y + a.Height * i / 4), new Point(a.Right, a.Y + a.Height * i / 4));
        }

        DrawHistogram(context, a);
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1, new DashStyle([3, 3], 0)), ToScreen(0, 0), ToScreen(1, 1));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var steps = Math.Max(64, (int)a.Width);
            g.BeginFigure(ToScreen(0, Evaluate(_points, 0)), false);
            for (var i = 1; i <= steps; i++)
            {
                var x = (double)i / steps;
                g.LineTo(ToScreen(x, Evaluate(_points, x)));
            }

            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(236, 236, 236)), 1.6), geometry);
        var accent = Resource("PF.Brush.Accent", Brushes.SteelBlue);
        for (var i = 0; i < _points.Count; i++)
        {
            var removing = i == _drag && _outside;
            using (context.PushOpacity(removing ? 0.35 : 1))
            {
                context.DrawEllipse(i == _drag ? accent : new SolidColorBrush(Color.FromRgb(40, 40, 40)), new Pen(Brushes.White, 1.2),
                    ToScreen(_points[i].X, _points[i].Y), Radius, Radius);
            }
        }

        DrawClipping(context, a);
    }

    private IBrush Resource(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private void DrawHistogram(DrawingContext context, Rect a)
    {
        if (Histogram is not { Length: >= 3 } h || h[0].Length < 2)
        {
            return;
        }

        var bins = h[0].Length;
        var counts = new double[3][];
        var all = new List<double>();
        for (var c = 0; c < 3; c++)
        {
            counts[c] = h[c].Select(v => Math.Sqrt(v)).ToArray(); // square root: peaks do not flatten the rest
            all.AddRange(counts[c].Skip(1).Take(bins - 2));
        }

        all.Sort();
        var top = Math.Max(all.Count > 0 ? all[Math.Clamp((int)(all.Count * 0.995), 0, all.Count - 1)] : 1, 1e-9);
        Color[] colors = [Color.FromArgb(120, 210, 70, 70), Color.FromArgb(120, 70, 190, 80), Color.FromArgb(120, 80, 120, 230)];
        for (var c = 0; c < 3; c++)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(a.X, a.Bottom), true);
                for (var i = 0; i < bins; i++)
                {
                    g.LineTo(new Point(a.X + a.Width * (i + 0.5) / bins, a.Bottom - a.Height * Math.Min(counts[c][i] / top, 1)));
                }

                g.LineTo(new Point(a.Right, a.Bottom));
                g.EndFigure(true);
            }

            context.DrawGeometry(new SolidColorBrush(colors[c]), null, geometry);
        }
    }

    private void DrawClipping(DrawingContext context, Rect a)
    {
        if (Histogram is not { Length: >= 4 } h || h[3].Length < 2)
        {
            return;
        }

        var total = Math.Max(h[3].Sum(v => (double)v), 1);
        var dark = Math.Max(h[0][0], Math.Max(h[1][0], h[2][0])) / total > ClipWarning;
        var last = h[0].Length - 1;
        var bright = Math.Max(h[0][last], Math.Max(h[1][last], h[2][last])) / total > ClipWarning;
        Triangle(context, a.X + 1, 1, dark);
        Triangle(context, a.Right - 1, -1, bright);
    }

    private static void Triangle(DrawingContext context, double x, int direction, bool on)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(x, 1), true);
            g.LineTo(new Point(x + 9 * direction, 1));
            g.LineTo(new Point(x, 10));
            g.EndFigure(true);
        }

        context.DrawGeometry(on ? Brushes.White : new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), null, geometry);
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var position = e.GetPosition(this);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2 && PointAt(position) is { } remove && remove > 0 && remove < _points.Count - 1)
        {
            _points.RemoveAt(remove);
            _drag = null;
            InvalidateVisual();
            CurveEdited?.Invoke(this, _points);
            return;
        }

        var index = PointAt(position);
        if (index is null)
        {
            var c = ToCurve(position);
            if (_points.Count >= MaxPoints || c.X <= 0 || c.X >= 1 || _points.Any(p => Math.Abs(p.X - c.X) < MinGap))
            {
                return;
            }

            var added = new Point(c.X, Evaluate(_points, c.X));
            _points.Add(added);
            _points = [.. _points.OrderBy(p => p.X)];
            index = _points.IndexOf(added);
        }

        _drag = index;
        _outside = false;
        e.Pointer.Capture(this);
        Move(position);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not null)
        {
            Move(e.GetPosition(this));
        }
    }

    private void Move(Point position)
    {
        if (_drag is not { } i)
        {
            return;
        }

        var c = ToCurve(position);
        var last = _points.Count - 1;
        var inner = i > 0 && i < last;
        _outside = inner && (c.Y < -0.15 || c.Y > 1.15 || c.X < -0.1 || c.X > 1.1);
        var y = Math.Clamp(c.Y, 0, 1);
        var x = inner ? Math.Clamp(c.X, _points[i - 1].X + MinGap, _points[i + 1].X - MinGap) : _points[i].X;
        _points[i] = new Point(x, y);
        InvalidateVisual();
        CurveChanged?.Invoke(this, VisiblePoints());
    }

    private List<Point> VisiblePoints() => [.. _points.Where((_, i) => !(i == _drag && _outside))];

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not { } i)
        {
            return;
        }

        if (_outside)
        {
            _points.RemoveAt(i);
        }

        _drag = null;
        _outside = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
        CurveEdited?.Invoke(this, _points);
    }
}
