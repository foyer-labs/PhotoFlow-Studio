// SPDX-License-Identifier: Apache-2.0

using Avalonia.Input;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Commands;

/// <summary>Global shortcuts of spec §12bis: Ctrl 1-7 sections, Ctrl K search, Ctrl I import folder, Ctrl , settings.</summary>
public static class GlobalCommands
{
    public const string Navigate = "Global.Navigate";
    public const string FocusSearch = "Global.FocusSearch";
    public const string OpenSettings = "Global.OpenSettings";
    public const string ImportFolder = "Global.ImportFolder";

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
        commands.Add(new(OpenSettings, new KeyGesture(Key.OemComma, KeyModifiers.Control), "Command.Settings", StatusGroupKey: "Command.Settings"));
        return new CommandTable(commands);
    }
}
