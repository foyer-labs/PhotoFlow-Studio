// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PhotoFlow.App.Views;

/// <summary>Resolves an icon resource key (e.g. <c>PF.Icon.Home</c>) to its geometry.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || Application.Current is not { } app)
        {
            return null;
        }

        return app.TryGetResource(key, app.ActualThemeVariant, out var resource) ? resource as Geometry : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
