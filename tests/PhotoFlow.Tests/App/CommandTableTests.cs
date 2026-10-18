// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PhotoFlow.App.Commands;
using PhotoFlow.App.ViewModels;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>Key bindings and status bar come from the same command table (CLAUDE.md §5).</summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class CommandTableTests
{
    [Fact]
    public void Global_table_has_unique_gestures_and_the_spec_shortcuts()
    {
        var table = GlobalCommands.Create();
        Assert.Equal(table.Commands.Count, table.Commands.Select(c => c.Gesture).Distinct().Count());

        var gestures = table.Commands.Select(c => c.Gesture.ToString()).ToList();
        Assert.Contains("Ctrl+K", gestures);
        Assert.Contains("Ctrl+OemComma", gestures);
        for (var digit = 1; digit <= 7; digit++)
        {
            Assert.Contains($"Ctrl+D{digit}", gestures);
        }
    }

    // KeyBinding is an AvaloniaObject: it must be created on the headless dispatcher thread.
    [AvaloniaFact]
    public void Key_bindings_are_generated_for_every_command()
    {
        var viewModel = new MainWindowViewModel();
        var bindings = viewModel.Commands.CreateKeyBindings(viewModel.ResolveCommand);
        Assert.Equal(viewModel.Commands.Commands.Count, bindings.Count);
        Assert.All(bindings, b => Assert.NotNull(b.Command));
    }

    [Fact]
    public void Status_hints_are_generated_from_the_same_table()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("it-IT");
        try
        {
            var hints = GlobalCommands.Create().CreateStatusHints();
            Assert.Equal(
                [new StatusHint("Ctrl K", "Cerca"), new StatusHint("Ctrl 1–7", "Sezioni"), new StatusHint("Ctrl I", "Importa cartella"), new StatusHint("Ctrl ,", "Impostazioni")],
                hints);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Duplicate_gestures_are_rejected()
    {
        var gesture = new KeyGesture(Key.P);
        Assert.Throws<ArgumentException>(() => new CommandTable([new("a", gesture, "x"), new("b", gesture, "y")]));
    }

    [Fact]
    public void Section_shortcuts_navigate()
    {
        var viewModel = new MainWindowViewModel();
        var navigate = viewModel.ResolveCommand(GlobalCommands.Navigate);
        navigate.Execute(SectionCatalog.Review);
        Assert.Equal(SectionCatalog.Review, viewModel.SelectedSection.Id);
        viewModel.ResolveCommand(GlobalCommands.OpenSettings).Execute(null);
        Assert.Equal(SectionCatalog.Settings, viewModel.SelectedSection.Id);
        Assert.Null(viewModel.SelectedMainSection);
        Assert.Equal(SectionCatalog.Settings, viewModel.SelectedSecondarySection?.Id);
    }

    [Theory]
    [InlineData(1920, false, false)]
    [InlineData(1281, false, false)]
    [InlineData(1280, false, true)]
    [InlineData(1920, true, true)]
    public void Navigation_collapses_automatically_at_1280(double width, bool collapsedByUser, bool expectedCompact)
    {
        var viewModel = new MainWindowViewModel { WindowWidth = width, IsNavigationCollapsedByUser = collapsedByUser };
        Assert.Equal(expectedCompact, viewModel.IsNavigationCompact);
    }
}
