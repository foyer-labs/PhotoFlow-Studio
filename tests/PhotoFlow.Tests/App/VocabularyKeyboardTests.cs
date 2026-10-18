// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Core.Paths;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// Vocabulary (5c, 5d, 5d2) with the keyboard: the core not formed yet; in "All" Enter approves and moves on, F2 edits
/// the Italian label, B blacklists, K changes category, Ctrl Z undoes; a personal tag is created and saved.
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class VocabularyKeyboardTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-vocabulary-keys");

    [AvaloniaFact]
    public async Task Rows_are_reviewed_and_a_personal_tag_is_saved_with_the_keyboard()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        await using var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None);
        await host.UpdateSettingsAsync(s => s with { SetupCompleted = true }, CancellationToken.None);
        using var shell = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        shell.ShowSection(SectionCatalog.Vocabulary);
        var vocabulary = Assert.IsType<VocabularyViewModel>(shell.CurrentPage);
        await WaitAsync(() => vocabulary.CoreText.Length > 0);

        // 5d2: no photo has tags yet.
        Assert.True(vocabulary.IsCoreNotReady);
        Assert.Empty(vocabulary.Rows);

        // All 4,585 tags of the shipped vocabulary.
        await vocabulary.SetTabCommand.ExecuteAsync("All");
        Assert.Equal(4585, vocabulary.Rows.Count);
        Focus(window);
        var first = vocabulary.Selected!;
        Assert.False(first.Tag.Reviewed);

        // Enter: approved as proposed, next row.
        Press(window, PhysicalKey.Enter);
        await WaitAsync(() => first.Tag.Reviewed);
        Assert.NotEqual(first, vocabulary.Selected);
        var second = vocabulary.Selected!;

        // F2: edit the label, Enter approves it.
        Press(window, PhysicalKey.F2);
        Assert.True(vocabulary.IsEditing);
        vocabulary.EditText = "etichetta corretta";
        Press(window, PhysicalKey.Enter);
        await WaitAsync(() => second.Tag.Reviewed && second.Tag.Italian == "etichetta corretta");

        // B, K on the next one; Ctrl Z undoes the last change (the category).
        var third = vocabulary.Selected!;
        var category = third.Tag.Category;
        Press(window, PhysicalKey.B);
        await WaitAsync(() => third.Tag.Blacklisted);
        Press(window, PhysicalKey.K);
        await WaitAsync(() => third.Tag.Category != category);
        Press(window, PhysicalKey.Z, RawInputModifiers.Control);
        await WaitAsync(() => third.Tag.Category == category && third.Tag.Blacklisted);
        var stored = await host.Catalog.Tags.FindAsync(third.Tag.Key, CancellationToken.None);
        Assert.True(stored!.Blacklisted);
        Assert.Equal(category, stored.Category);

        // 5d: a personal tag (CLIP missing here: saved, assigned when the model is installed).
        Press(window, PhysicalKey.N, RawInputModifiers.Control);
        Assert.True(vocabulary.IsCustom);
        vocabulary.CustomLabel = "Lago di Como";
        vocabulary.CustomDescription = "a photo of Lake Como with mountains and villas on the shore";
        vocabulary.CustomCategory = "Luoghi";
        Focus(window);
        Press(window, PhysicalKey.S, RawInputModifiers.Control);
        await WaitAsync(() => vocabulary.CustomTags.Count == 1);
        var (tag, custom) = Assert.Single(await host.Catalog.Tags.ListCustomAsync(CancellationToken.None));
        Assert.Equal(("custom:lago-di-como", "Lago di Como", "Luoghi"), (tag.Key, tag.Italian, tag.Category));
        Assert.Equal(0.27, custom.Threshold, 6);
    }

    public void Dispose() => _temp.Dispose();

    private static void Focus(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendantsOf<VocabularyView>()?.Focus();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
