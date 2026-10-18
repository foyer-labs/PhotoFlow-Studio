// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

/// <summary>Installs the page's command table as key bindings of the page view and gives it the keyboard focus.</summary>
public static class PageKeys
{
    public static void Attach(UserControl view)
    {
        ArgumentNullException.ThrowIfNull(view);
        view.Focusable = true;
        view.DataContextChanged += (_, _) => Install(view);
        view.AttachedToVisualTree += (_, _) => view.Focus();
    }

    private static void Install(UserControl view)
    {
        view.KeyBindings.Clear();
        if (view.DataContext is PageViewModel { Commands: { } table } page)
        {
            foreach (var binding in table.CreateKeyBindings(page.ResolveCommand))
            {
                if (binding.Command is { } command && binding.Gesture is { } gesture && IsTyping(gesture))
                {
                    binding.Command = new TypingGuard(command, view);
                }

                view.KeyBindings.Add(binding);
            }
        }
    }

    /// <summary>Single keys that also type text (letters, digits, space, arrows, Delete): a text field keeps them.</summary>
    private static bool IsTyping(KeyGesture gesture) =>
        (gesture.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0
        && gesture.Key is not (Key.Escape or Key.Enter) and not (>= Key.F1 and <= Key.F24);

    /// <summary>Disables a typing key's command while the keyboard focus is in a text field of the page.</summary>
    private sealed class TypingGuard(ICommand inner, Control view) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add => inner.CanExecuteChanged += value;
            remove => inner.CanExecuteChanged -= value;
        }

        public bool CanExecute(object? parameter) =>
            TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement() is not TextBox && inner.CanExecute(parameter);

        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                inner.Execute(parameter);
            }
        }
    }
}
