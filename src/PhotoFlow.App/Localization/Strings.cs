// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Resources;
using Avalonia.Markup.Xaml;

namespace PhotoFlow.App.Localization;

/// <summary>
/// UI texts from the IT/EN resource files (neutral = English, <c>Strings.it.resx</c> = Italian).
/// The UI culture is chosen at startup (system language by default, D10) and applies on window restart (P18).
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Resources = new("PhotoFlow.App.Localization.Strings", typeof(Strings).Assembly);

    public static string Get(string key) =>
        Resources.GetString(key, CultureInfo.CurrentUICulture) ?? $"[{key}]";

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}

/// <summary>XAML markup extension: <c>Text="{l:Tr Nav.Home}"</c>.</summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
