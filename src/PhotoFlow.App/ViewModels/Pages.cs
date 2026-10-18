// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;
using Avalonia.Threading;
using PhotoFlow.App.Commands;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoFlow.App.Localization;

namespace PhotoFlow.App.ViewModels;

/// <summary>Content of the main area for a section (or a flow opened from it, such as the import procedure).</summary>
public abstract class PageViewModel : ObservableObject
{
    /// <summary>Text of the toolbar above the content.</summary>
    public abstract string Title { get; }

    /// <summary>Called when the page becomes visible (refresh of catalog data).</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;

    /// <summary>
    /// Shortcuts of the page (CLAUDE.md §5): bound on the page view, so letters typed in a text field elsewhere never
    /// trigger them; the status bar shows the same table.
    /// </summary>
    public virtual CommandTable? Commands => null;

    public virtual ICommand ResolveCommand(string id) => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown page command.");
}

/// <summary>Navigation requests from the pages to the window shell.</summary>
public interface IShellNavigator
{
    void ShowSection(string sectionId);

    void ShowPage(PageViewModel page);

    /// <summary>Opens the review of a job.</summary>
    void OpenReview(long jobId);
}

/// <summary>
/// A section whose functions arrive in a later phase (decision F1-6, c1): the place is visible, the controls are not
/// active, the text says when they arrive.
/// </summary>
public sealed class PlaceholderPageViewModel(Section section, string phase) : PageViewModel
{
    /// <summary>Phase that brings each section (spec §13).</summary>
    public static readonly IReadOnlyDictionary<string, string> Phases = new Dictionary<string, string>
    {
    };

    public Section Section { get; } = section;

    public override string Title => Section.Label;

    public string Message => Strings.Format("Page.ComesWithPhase", phase);
}

/// <summary>Runs view-model updates raised by background services on the UI thread.</summary>
public static class Ui
{
    public static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
