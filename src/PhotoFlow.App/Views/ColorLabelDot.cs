// SPDX-License-Identifier: Apache-2.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PhotoFlow.App.Views;

/// <summary>
/// The colour label of a photo (M4) as a small dot, with the colour of the token <c>PF.Brush.Label.&lt;name&gt;</c>;
/// nothing drawn without a label.
/// </summary>
public sealed class ColorLabelDot : Control
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<ColorLabelDot, string?>(nameof(Label));

    static ColorLabelDot()
    {
        AffectsRender<ColorLabelDot>(LabelProperty);
        AffectsMeasure<ColorLabelDot>(LabelProperty);
    }

    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => string.IsNullOrEmpty(Label) ? default : new Size(10, 10);

    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Label))
        {
            return;
        }

        var brush = this.TryFindResource("PF.Brush.Label." + Label, ActualThemeVariant, out var value) && value is IBrush b ? b : Brushes.Gray;
        context.DrawEllipse(brush, null, new Rect(0, (Bounds.Height - 10) / 2, 10, 10));
    }
}
