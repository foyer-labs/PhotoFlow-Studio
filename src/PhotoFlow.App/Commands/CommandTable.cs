// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;
using Avalonia.Input;
using PhotoFlow.App.Localization;

namespace PhotoFlow.App.Commands;

/// <summary>
/// One command of a view: id, gesture, label and optional status-bar group. Commands sharing a group
/// collapse into a single status-bar hint (e.g. "Ctrl 1–7 Sections").
/// </summary>
public sealed record CommandDefinition(string Id, KeyGesture Gesture, string LabelKey, string? StatusGroupKey = null, object? Parameter = null);

/// <summary>A key hint shown in the status bar.</summary>
public sealed record StatusHint(string Keys, string Label);

/// <summary>
/// The single command table of a view (CLAUDE.md §5): both the <see cref="KeyBinding"/>s and the
/// status-bar hints are generated from it, so they can never diverge.
/// </summary>
public sealed class CommandTable
{
    public CommandTable(IEnumerable<CommandDefinition> commands)
    {
        Commands = commands.ToList();
        var duplicate = Commands.GroupBy(c => c.Gesture).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Gesture {duplicate.Key} is assigned to more than one command.", nameof(commands));
        }
    }

    public IReadOnlyList<CommandDefinition> Commands { get; }

    public IReadOnlyList<KeyBinding> CreateKeyBindings(Func<string, ICommand> resolveCommand)
    {
        ArgumentNullException.ThrowIfNull(resolveCommand);
        return Commands.Select(c =>
            {
                var binding = new KeyBinding { Gesture = c.Gesture, Command = resolveCommand(c.Id) };
                if (c.Parameter is not null)
                {
                    binding.CommandParameter = c.Parameter;
                }

                return binding;
            })
            .ToList();
    }

    public IReadOnlyList<StatusHint> CreateStatusHints()
    {
        var hints = new List<StatusHint>();
        foreach (var group in Commands.Where(c => c.StatusGroupKey is not null).GroupBy(c => c.StatusGroupKey!))
        {
            var items = group.ToList();
            var keys = items.Count == 1
                ? FormatGesture(items[0].Gesture)
                : IsDigitRange(items)
                    ? $"{FormatGesture(items[0].Gesture)}–{FormatKey(items[^1].Gesture.Key)}"
                    : string.Join(' ', items.Select(i => FormatGesture(i.Gesture)));
            hints.Add(new StatusHint(keys, Strings.Get(group.Key)));
        }

        return hints;
    }

    private static bool IsDigitRange(IReadOnlyList<CommandDefinition> items) =>
        items.All(i => i.Gesture.Key is >= Key.D0 and <= Key.D9);

    /// <summary>Display form used across the UI: "Ctrl K", "Ctrl ,", "Maiusc R".</summary>
    public static string FormatGesture(KeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add(Strings.Get("Key.Ctrl"));
        }

        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add(Strings.Get("Key.Shift"));
        }

        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add(Strings.Get("Key.Alt"));
        }

        parts.Add(FormatKey(gesture.Key));
        return string.Join(' ', parts);
    }

    private static string FormatKey(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        Key.OemComma => ",",
        Key.Return => "↵",
        Key.Escape => "Esc",
        Key.Space => Strings.Get("Key.Space"),
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Delete => Strings.Get("Key.Delete"),
        Key.Back => "⌫",
        Key.PageUp => Strings.Get("Key.PageUp"),
        Key.PageDown => Strings.Get("Key.PageDown"),
        _ => key.ToString(),
    };
}
