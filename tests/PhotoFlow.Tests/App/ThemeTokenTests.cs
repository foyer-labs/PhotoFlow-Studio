// SPDX-License-Identifier: Apache-2.0

using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>Every color token of spec §12bis exists in both theme variants with the specified value.</summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class ThemeTokenTests
{
    // Key, dark, light — copied from spec §12bis (tables "Colori" and "Esiti del culling") and board 01.
    public static TheoryData<string, string, string> ColorTokens => new()
    {
        { "PF.Color.Viewer", "#0E0E0E", "#2A2A2A" },
        { "PF.Color.GridArea", "#151515", "#DCDCDC" },
        { "PF.Color.Window", "#1B1B1B", "#F2F2F2" },
        { "PF.Color.Panel", "#222222", "#FAFAFA" },
        { "PF.Color.Control", "#2B2B2B", "#E8E8E8" },
        { "PF.Color.ControlHover", "#363636", "#DEDEDE" },
        { "PF.Color.Border", "#303030", "#D4D4D4" },
        { "PF.Color.BorderStrong", "#4A4A4A", "#ABABAB" },
        { "PF.Color.TextPrimary", "#EDEDED", "#1A1A1A" },
        { "PF.Color.TextSecondary", "#A6A6A6", "#4F4F4F" },
        { "PF.Color.TextTertiary", "#808080", "#6B6B6B" },
        { "PF.Color.Accent", "#5E9EEA", "#1F6FD1" },
        { "PF.Color.PrimaryAction", "#E6E6E6", "#1A1A1A" },
        { "PF.Color.PrimaryActionText", "#111111", "#FAFAFA" },
        { "PF.Color.Outcome.Discard", "#EB5A50", "#C8372D" },
        { "PF.Color.Outcome.ToReview", "#E9B13C", "#A86F00" },
        { "PF.Color.Outcome.Best", "#4CC38A", "#1E8A55" },
        { "PF.Color.Outcome.Similar", "#9C9AE0", "#5F5BB8" },
        { "PF.Color.Outcome.Kept", "#3A3A3A", "#D4D4D4" },
    };

    [AvaloniaTheory]
    [MemberData(nameof(ColorTokens))]
    public void Color_token_has_the_specified_value_in_both_variants(string key, string dark, string light)
    {
        var app = Application.Current!;
        Assert.True(app.TryGetResource(key, ThemeVariant.Dark, out var darkValue), $"{key} missing in Dark");
        Assert.True(app.TryGetResource(key, ThemeVariant.Light, out var lightValue), $"{key} missing in Light");
        Assert.Equal(Color.Parse(dark), Assert.IsType<Color>(darkValue));
        Assert.Equal(Color.Parse(light), Assert.IsType<Color>(lightValue));
    }

    [AvaloniaTheory]
    [MemberData(nameof(ColorTokens))]
    public void Brush_token_matches_its_color_in_both_variants(string key, string dark, string light)
    {
        var brushKey = key.Replace("PF.Color.", "PF.Brush.", StringComparison.Ordinal);
        foreach (var (variant, hex) in new[] { (ThemeVariant.Dark, dark), (ThemeVariant.Light, light) })
        {
            Assert.True(Application.Current!.TryGetResource(brushKey, variant, out var value), $"{brushKey} missing in {variant}");
            Assert.Equal(Color.Parse(hex), Assert.IsAssignableFrom<ISolidColorBrush>(value).Color);
        }
    }

    [AvaloniaTheory]
    [InlineData("PF.Brush.State.Info", "PF.Color.Accent")]
    [InlineData("PF.Brush.State.Warning", "PF.Color.Outcome.ToReview")]
    [InlineData("PF.Brush.State.Error", "PF.Color.Outcome.Discard")]
    public void System_state_brushes_reuse_the_board_01_colors(string brushKey, string colorKey)
    {
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Assert.True(Application.Current!.TryGetResource(brushKey, variant, out var brush));
            Assert.True(Application.Current!.TryGetResource(colorKey, variant, out var color));
            Assert.Equal(Assert.IsType<Color>(color), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
        }
    }

    [AvaloniaFact]
    public void Default_theme_is_dark() =>
        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);

    [AvaloniaFact]
    public void Typography_spacing_and_structure_tokens_match_the_spec()
    {
        var app = Application.Current!;
        (string Key, double Value)[] expected =
        [
            ("PF.FontSize.PageTitle", 24), ("PF.LineHeight.PageTitle", 32),
            ("PF.FontSize.SectionTitle", 18), ("PF.FontSize.Subtitle", 15),
            ("PF.FontSize.Body", 13), ("PF.LineHeight.Body", 20),
            ("PF.FontSize.Label", 12), ("PF.FontSize.Data", 12), ("PF.FontSize.Micro", 11),
            ("PF.Space.4", 4), ("PF.Space.8", 8), ("PF.Space.12", 12), ("PF.Space.16", 16),
            ("PF.Space.24", 24), ("PF.Space.32", 32), ("PF.Space.48", 48),
            ("PF.Size.TitleBar", 32), ("PF.Size.Navigation", 184), ("PF.Size.NavigationCompact", 48),
            ("PF.Size.Toolbar", 40), ("PF.Size.DetailsPanel", 300), ("PF.Size.StatusBar", 24),
            ("PF.Size.SettingsColumnMax", 760), ("PF.Size.ButtonMinWidth", 72),
        ];

        foreach (var (key, value) in expected)
        {
            Assert.True(app.TryGetResource(key, ThemeVariant.Dark, out var actual), $"{key} missing");
            Assert.Equal(value, Assert.IsType<double>(actual));
        }

        Assert.True(app.TryGetResource("PF.Radius.Photo", null, out var photo));
        Assert.Equal(new CornerRadius(2), photo);
        Assert.True(app.TryGetResource("PF.Radius.Control", null, out var control));
        Assert.Equal(new CornerRadius(4), control);
        Assert.True(app.TryGetResource("PF.Radius.Dialog", null, out var dialog));
        Assert.Equal(new CornerRadius(8), dialog);
    }

    [AvaloniaFact]
    public void Embedded_fonts_resolve_to_ibm_plex()
    {
        var app = Application.Current!;
        foreach (var (key, family) in new[] { ("PF.Font.Sans", "IBM Plex Sans"), ("PF.Font.Mono", "IBM Plex Mono") })
        {
            Assert.True(app.TryGetResource(key, null, out var value));
            var fontFamily = Assert.IsType<FontFamily>(value);
            var typeface = new Typeface(fontFamily);
            Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface), $"{family} not loadable");
            Assert.Equal(family, glyphTypeface.FamilyName);
        }
    }
}
