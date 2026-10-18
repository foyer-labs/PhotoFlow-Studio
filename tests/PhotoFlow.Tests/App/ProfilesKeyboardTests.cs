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

/// <summary>Profiles (board 5e) with the keyboard: built-in locked, Ctrl D duplicates, edit, Esc undoes, Ctrl S saves a new version.</summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class ProfilesKeyboardTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-profiles-keys");

    [AvaloniaFact]
    public async Task A_built_in_profile_is_duplicated_edited_and_saved()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        await using var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None);
        await host.UpdateSettingsAsync(s => s with { SetupCompleted = true }, CancellationToken.None);
        using var shell = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        shell.ShowSection(SectionCatalog.Profiles);
        var profiles = Assert.IsType<ProfilesViewModel>(shell.CurrentPage);
        await Settle(profiles.ActivateAsync());
        Focus(window);

        // Built-in: locked, Ctrl S does nothing.
        Assert.Equal("events", profiles.Selected!.Profile.Id);
        Assert.True(profiles.IsLocked);
        Press(window, PhysicalKey.ArrowDown);
        Assert.Equal("portrait", profiles.Selected!.Profile.Id);
        Press(window, PhysicalKey.ArrowUp);
        Press(window, PhysicalKey.S, RawInputModifiers.Control);
        Assert.Equal(7, host.Profiles.All.Count);

        // Ctrl D: a custom copy, selected and editable.
        Press(window, PhysicalKey.D, RawInputModifiers.Control);
        await WaitAsync(() => profiles.IsEditable);
        var id = profiles.Selected!.Profile.Id;
        Assert.StartsWith("custom-", id, StringComparison.Ordinal);
        Assert.Equal("events", profiles.Selected.Profile.BasedOn);

        // Weights that do not sum to 100 are refused; Esc undoes.
        profiles.WeightSharpness = 50;
        Assert.True(profiles.IsDirty);
        Assert.False(profiles.WeightsValid);
        Press(window, PhysicalKey.Escape);
        Assert.False(profiles.IsDirty);
        Assert.Equal(35, profiles.WeightSharpness);

        // A valid change saved with Ctrl S becomes version 2.
        profiles.WeightSharpness = 40;
        profiles.WeightEyes = 25;
        profiles.BlurDiscard = 0.30m;
        profiles.MaxBurstGap = 1.5m;
        Assert.True(profiles.WeightsValid);
        Press(window, PhysicalKey.S, RawInputModifiers.Control);
        await WaitAsync(() => host.Profiles.Find(id)?.Version == 2);
        var saved = host.Profiles.Get(id);
        Assert.Equal(40, saved.Weights.Sharpness);
        Assert.Equal(0.30, saved.Thresholds.BlurDiscard, 6);
        Assert.Equal(1.5, saved.Grouping.MaxBurstGapSeconds, 6);
        Assert.False(profiles.IsDirty);

        // The custom profile is offered by the import procedure and named by the host.
        Assert.Contains(host.Profiles.All, p => p.Id == id);
        Assert.Equal(saved.Name, host.ProfileName(id));
    }

    public void Dispose() => _temp.Dispose();

    private static void Focus(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendantsOf<ProfilesView>()?.Focus();
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

    private static async Task Settle(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        await task;
        Dispatcher.UIThread.RunJobs();
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
