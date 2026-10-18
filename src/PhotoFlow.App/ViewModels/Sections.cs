// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Localization;

namespace PhotoFlow.App.ViewModels;

/// <summary>A navigation section of the window shell (board 04).</summary>
public sealed record Section(string Id, string LabelKey, string IconKey, int? ShortcutDigit, bool IsSecondary)
{
    public string Label => Strings.Get(LabelKey);

    public string? ShortcutText => ShortcutDigit is int digit ? $"{Strings.Get("Key.Ctrl")} {digit}" : null;
}

public static class SectionCatalog
{
    public const string Home = "home";
    public const string Jobs = "jobs";
    public const string Review = "review";
    public const string People = "people";
    public const string Browse = "browse";
    public const string Duplicates = "duplicates";
    public const string Libraries = "libraries";
    public const string Vocabulary = "vocabulary";
    public const string Profiles = "profiles";
    public const string Settings = "settings";

    /// <summary>Main sections take Ctrl 1-7 in order; settings use Ctrl , (spec §12bis).</summary>
    public static IReadOnlyList<Section> All { get; } =
    [
        new(Home, "Nav.Home", "PF.Icon.Home", 1, false),
        new(Jobs, "Nav.Jobs", "PF.Icon.Jobs", 2, false),
        new(Review, "Nav.Review", "PF.Icon.Review", 3, false),
        new(People, "Nav.People", "PF.Icon.People", 4, false),
        new(Browse, "Nav.Browse", "PF.Icon.Search", 5, false),
        new(Duplicates, "Nav.Duplicates", "PF.Icon.Duplicates", 6, false),
        new(Libraries, "Nav.Libraries", "PF.Icon.Libraries", 7, true),
        new(Vocabulary, "Nav.Vocabulary", "PF.Icon.Vocabulary", null, true),
        new(Profiles, "Nav.Profiles", "PF.Icon.Profiles", null, true),
        new(Settings, "Nav.Settings", "PF.Icon.Settings", null, true),
    ];
}
