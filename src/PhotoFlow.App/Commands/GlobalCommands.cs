// SPDX-License-Identifier: Apache-2.0

using Avalonia.Input;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Commands;

/// <summary>Global shortcuts of spec §12bis: Ctrl 1-9 sections, Ctrl K search, Ctrl I import folder, Ctrl , settings.</summary>
public static class GlobalCommands
{
    public const string Navigate = "Global.Navigate";
    public const string FocusSearch = "Global.FocusSearch";
    public const string OpenSettings = "Global.OpenSettings";
    public const string ImportFolder = "Global.ImportFolder";
    public const string Export = "Global.Export";
    public const string FullScreen = "Global.FullScreen";

    public static CommandTable Create()
    {
        var commands = new List<CommandDefinition>
        {
            new(FocusSearch, new KeyGesture(Key.K, KeyModifiers.Control), "Command.Search", StatusGroupKey: "Command.Search"),
        };

        var shortcutSections = SectionCatalog.All.Where(s => s.ShortcutDigit is not null).OrderBy(s => s.ShortcutDigit);
        foreach (var section in shortcutSections)
        {
            commands.Add(new CommandDefinition(
                Navigate,
                new KeyGesture(Key.D0 + section.ShortcutDigit!.Value, KeyModifiers.Control),
                section.LabelKey,
                StatusGroupKey: "Command.Sections",
                Parameter: section.Id));
        }

        commands.Add(new(ImportFolder, new KeyGesture(Key.I, KeyModifiers.Control), "Home.QuickAction.ImportFolder", StatusGroupKey: "Home.QuickAction.ImportFolder"));
        // Decision S13: export the photos of the current page from anywhere.
        commands.Add(new(Export, new KeyGesture(Key.E, KeyModifiers.Control | KeyModifiers.Shift), "Command.Export", StatusGroupKey: "Command.Export"));
        commands.Add(new(OpenSettings, new KeyGesture(Key.OemComma, KeyModifiers.Control), "Command.Settings", StatusGroupKey: "Command.Settings"));
        // M9: the culling (and everything else) on the whole screen.
        commands.Add(new(FullScreen, new KeyGesture(Key.F11), "Command.FullScreen"));
        return new CommandTable(commands);
    }
}
