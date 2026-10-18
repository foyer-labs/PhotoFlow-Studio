// SPDX-License-Identifier: Apache-2.0

using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Data;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>Settings (boards 5f-5j) through the real view, and the catalog restore it schedules.</summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class SettingsTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-settings");

    [Fact]
    public async Task Concurrent_settings_saves_leave_the_latest_settings_on_disk()
    {
        var paths = new DefaultAppPaths(_temp.Sub("concurrent"));
        await using (var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None))
        {
            // Unawaited saves, as the settings view does: the file must end with every change, never an older snapshot.
            var saves = new List<Task>();
            for (var i = 0; i < 20; i++)
            {
                saves.Add(host.UpdateSettingsAsync(s => s with { Language = "en" }, CancellationToken.None));
                saves.Add(host.UpdateSettingsAsync(s => s with { Theme = ThemePreference.Light }, CancellationToken.None));
                saves.Add(host.UpdateSettingsAsync(s => s with { PrudentMode = !s.PrudentMode }, CancellationToken.None));
            }

            await Task.WhenAll(saves);
        }

        Assert.Equal(("en", ThemePreference.Light), SettingsStore.LoadStartupPreferences(paths));
    }

    [AvaloniaFact]
    public async Task Tabs_template_backup_and_preferences_through_the_view()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        await using (var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None))
        {
            using var shell = new MainWindowViewModel(host);
            var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
            window.Show();
            shell.ShowSection(SectionCatalog.Settings);
            var settings = Assert.IsType<SettingsViewModel>(shell.CurrentPage);
            await Settle(settings.ActivateAsync());
            Assert.True(settings.IsHardware);
            Save(window, "5f-settings-hardware");

            // Ctrl Tab / Ctrl Shift Tab move between the tabs (5f).
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
            Assert.True(settings.IsModels);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.True(settings.IsHardware);

            // 5g: phase 1 models can be downloaded, later ones are shown and disabled; nothing accepted for the user.
            Assert.Contains(settings.Models, m => m.Model.Id == "insightface-buffalo-l" && m.IsAvailable && m.RequiresAcceptance && !m.LicenseAccepted);
            Assert.Contains(settings.Models, m => m.Model.Id == "ramplus-fp32" && m.IsAvailable && !m.RequiresAcceptance);
            settings.SelectedTab = SettingsViewModel.ModelsTab;
            Save(window, "5g-settings-models");
            var buffalo = settings.Models.Single(m => m.Model.Id == "insightface-buffalo-l");
            if (buffalo.CanInstall)
            {
                await Settle(settings.InstallModelCommand.ExecuteAsync(buffalo));
                Assert.False(buffalo.IsInstalled);
                Assert.DoesNotContain("insightface-buffalo-l", host.Settings.AcceptedModelLicenses);
            }

            // 5i: an invalid template is shown and not saved; a valid one is saved and used by the import planner.
            settings.SelectedTab = SettingsViewModel.GeneralTab;
            settings.PathTemplate = "{yyyy}/{bogus}.{ext}";
            Assert.NotNull(settings.TemplateError);
            Assert.Equal(PathTemplate.Default, host.Settings.Import.PathTemplate);
            settings.PathTemplate = "{yyyy}/{evento}/{nomeoriginale}.{ext}";
            Assert.Null(settings.TemplateError);
            Assert.Contains("DSC_0001.NEF", settings.TemplateExample, StringComparison.Ordinal);
            await WaitAsync(() => host.Settings.Import.PathTemplate == "{yyyy}/{evento}/{nomeoriginale}.{ext}");
            Assert.Equal(Path.Combine("2026", "Prova", "IMG_7.JPG"),
                host.PathTemplate.Render(new PathTemplateContext("Prova", new DateOnly(2026, 1, 2), 3, null, "IMG_7", "JPG")));

            settings.Language = settings.Languages.Single(l => l.Value == "en");
            settings.Theme = settings.Themes.Single(t => t.Value == ThemePreference.Light);
            await WaitAsync(() => host.Settings.Language == "en" && host.Settings.Theme == ThemePreference.Light);
            Assert.NotNull(settings.ExifToolHealth);
            Save(window, "5i-settings-general");

            // 5h: backup now, then a restore scheduled for the next start.
            settings.SelectedTab = SettingsViewModel.CatalogTab;
            await Settle(settings.BackupNowCommand.ExecuteAsync(null));
            Assert.NotEmpty(settings.Backups);
            settings.SelectedBackup = settings.Backups[0];
            settings.RequestRestoreCommand.Execute(null);
            Assert.True(settings.HasPendingRestore);
            Save(window, "5h-settings-catalog");
            settings.CancelRestoreCommand.Execute(null);
            Assert.False(settings.HasPendingRestore);
            Assert.Null(host.Restore.Pending);

            settings.SelectedTab = SettingsViewModel.CullingTab;
            Assert.True(settings.PrudentMode);
            Save(window, "5j-settings-culling");
            PhotoFlow.App.App.ApplyTheme(ThemePreference.Dark);
            window.Close();
        }

        Assert.Equal(("en", ThemePreference.Light), SettingsStore.LoadStartupPreferences(paths));
    }

    [Fact]
    public async Task A_restore_chosen_in_the_settings_is_applied_at_the_next_start_and_the_old_catalog_is_kept()
    {
        var paths = new DefaultAppPaths(_temp.Sub("restore"));
        await using (var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None))
        {
            await host.Catalog.Libraries.AddAsync(new Library(0, "Prima", _temp.Sub("a"), WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), CancellationToken.None);
            var backup = await host.Maintenance.BackupNowAsync(host.Catalog, CancellationToken.None);
            await host.Catalog.Libraries.AddAsync(new Library(0, "Dopo", _temp.Sub("b"), WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), CancellationToken.None);
            host.Restore.Request(backup);

            // Closing writes a new backup and rotates: the chosen backup must survive.
            for (var i = 0; i < MaintenanceService.KeepBackups + 2; i++)
            {
                File.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddDays(-1));
                await host.Maintenance.BackupNowAsync(host.Catalog, CancellationToken.None);
            }

            Assert.True(File.Exists(backup));
        }

        await using (var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None))
        {
            Assert.True(host.RestoreOutcome!.Restored);
            Assert.Equal(["Prima"], (await host.Catalog.Libraries.ListAsync(CancellationToken.None)).Select(l => l.Name));
            Assert.Null(host.Restore.Pending);
            var replaced = Assert.Single(Directory.GetDirectories(paths.BackupsDirectory, "replaced-*"));
            Assert.True(File.Exists(Path.Combine(replaced, Path.GetFileName(paths.CatalogPath))));
        }

        // The replaced catalog still holds what was there before the restore.
        var kept = Directory.GetDirectories(paths.BackupsDirectory, "replaced-*").Single();
        await using var old = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(kept, Path.GetFileName(paths.CatalogPath)), _temp.Sub("scratch")), CancellationToken.None);
        Assert.Equal(2, (await old.Libraries.ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task A_damaged_backup_is_not_restored_and_the_catalog_opens_unchanged()
    {
        var paths = new DefaultAppPaths(_temp.Sub("damaged"));
        await using (var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None))
        {
            await host.Catalog.Libraries.AddAsync(new Library(0, "Unica", _temp.Sub("c"), WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), CancellationToken.None);
            var backup = await host.Maintenance.BackupNowAsync(host.Catalog, CancellationToken.None);
            host.Restore.Request(backup);
            await File.WriteAllBytesAsync(backup, new byte[8192], CancellationToken.None);
        }

        await using (var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None))
        {
            Assert.False(host.RestoreOutcome!.Restored);
            Assert.Equal(["Unica"], (await host.Catalog.Libraries.ListAsync(CancellationToken.None)).Select(l => l.Name));
            Assert.Null(host.Restore.Pending);
            Assert.Empty(Directory.GetDirectories(paths.BackupsDirectory, "replaced-*"));
        }
    }

    [Fact]
    public async Task A_damaged_catalog_at_startup_is_restored_from_the_last_backup_and_kept()
    {
        var paths = new DefaultAppPaths(_temp.Sub("corrupt"));
        await using (var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None))
        {
            await host.Catalog.Libraries.AddAsync(new Library(0, "Salvata", _temp.Sub("d"), WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), CancellationToken.None);
        }

        // Closing wrote a backup; now the catalog file gets damaged.
        var damaged = new byte[64 * 1024];
        new Random(5).NextBytes(damaged);
        await File.WriteAllBytesAsync(paths.CatalogPath, damaged, CancellationToken.None);
        var error = await Assert.ThrowsAsync<CatalogCorruptException>(() => PhotoFlowHost.StartAsync(paths, CancellationToken.None));

        PhotoFlowHost? restarted = null;
        var problem = new CatalogProblemViewModel(error.Message, new CatalogRestoreService(paths), async () => restarted = await PhotoFlowHost.StartAsync(paths, CancellationToken.None));
        Assert.True(problem.HasBackup);
        await problem.RestoreCommand.ExecuteAsync(null);

        Assert.NotNull(restarted);
        await using (restarted)
        {
            Assert.Equal(["Salvata"], (await restarted!.Catalog.Libraries.ListAsync(CancellationToken.None)).Select(l => l.Name));
        }

        var replaced = Assert.Single(Directory.GetDirectories(paths.BackupsDirectory, "replaced-*"));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(Path.Combine(replaced, Path.GetFileName(paths.CatalogPath)), CancellationToken.None));
    }

    [Fact]
    public void Startup_preferences_ignore_unknown_languages_and_a_missing_file()
    {
        var paths = new DefaultAppPaths(_temp.Sub("prefs"));
        Assert.Equal((null, ThemePreference.Dark), SettingsStore.LoadStartupPreferences(paths));
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, SettingsStore.FileName), """{ "language": "xx", "theme": "System" }""");
        Assert.Equal((null, ThemePreference.System), SettingsStore.LoadStartupPreferences(paths));
    }

    [AvaloniaFact]
    public async Task Normal_mode_stays_locked_without_metrics_and_the_password_opens_the_key()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile-culling"));
        await using var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None);
        var settings = new SettingsViewModel(host);
        await Settle(settings.ActivateAsync());

        // D23: no labelled set, no normal mode.
        Assert.False(settings.NormalModeAllowed);
        Assert.Equal(3, settings.CullingMetrics.Count);
        await Settle(settings.SetCullingModeCommand.ExecuteAsync("Normal"));
        Assert.True(host.Settings.PrudentMode);
        Assert.NotNull(settings.Message);

        // D12: a password is checked, then wraps the key for other accounts or PCs.
        settings.EncryptionPassword = "correct horse";
        settings.EncryptionConfirm = "correct hors";
        await Settle(settings.SetEncryptionPasswordCommand.ExecuteAsync(null));
        Assert.False(host.FingerprintKeys.HasPassword);
        settings.EncryptionConfirm = "correct horse";
        await Settle(settings.SetEncryptionPasswordCommand.ExecuteAsync(null));
        Assert.True(host.FingerprintKeys.HasPassword);
        Assert.Empty(settings.EncryptionPassword);
    }

    [AvaloniaFact]
    public async Task A_catalog_exported_from_the_settings_is_imported_elsewhere_at_the_next_start()
    {
        var root = _temp.Sub("photos");
        var moved = _temp.Sub("photos-moved");
        string package;
        long libraryId;
        var source = new DefaultAppPaths(_temp.Sub("profile-a"));
        await using (var host = await PhotoFlowHost.StartAsync(source, CancellationToken.None))
        {
            libraryId = await host.Catalog.Libraries.AddAsync(new Library(0, "Archivio", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), CancellationToken.None);
            var settings = new SettingsViewModel(host);
            await Settle(settings.ActivateAsync());
            settings.SelectedTab = SettingsViewModel.CatalogTab;
            var window = new Avalonia.Controls.Window { Content = new SettingsView { DataContext = settings }, Width = 1440, Height = 900 };
            window.Show();
            Save(window, "5h-settings-catalog");
            package = Path.Combine(_temp.Path, SettingsViewModel.ExportFileName);
            settings.ExportThumbnails = false;
            await Settle(settings.ExportCatalogAsync(package));
            Assert.True(File.Exists(package), settings.PackageText);
        }

        // The other PC: the library is elsewhere; the import waits for the next start.
        var destination = new DefaultAppPaths(_temp.Sub("profile-b"));
        await using (var host = await PhotoFlowHost.StartAsync(destination, CancellationToken.None))
        {
            var settings = new SettingsViewModel(host);
            await Settle(settings.ActivateAsync());
            await Settle(settings.LoadPackageAsync(package));
            Assert.True(settings.IsImporting);
            var row = Assert.Single(settings.ImportLibraries);
            Assert.Equal((root, "✓"), (row.NewRoot, row.Found));
            row.NewRoot = Path.Combine(moved, "missing");
            Assert.Equal("?", row.Found);
            row.NewRoot = moved;
            Assert.Equal("✓", row.Found);
            await Settle(settings.ConfirmImportCommand.ExecuteAsync(null));
            Assert.True(settings.HasPendingRestore, settings.PackageText);
            Assert.Empty(await host.Catalog.Libraries.ListAsync(CancellationToken.None)); // not yet
        }

        await using (var host = await PhotoFlowHost.StartAsync(destination, CancellationToken.None))
        {
            Assert.True(host.RestoreOutcome?.Restored);
            Assert.Equal(moved, (await host.Catalog.Libraries.GetAsync(libraryId, CancellationToken.None))!.RootPath);
        }
    }

    public void Dispose() => _temp.Dispose();

    private static void Save(Avalonia.Controls.Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var folder = Environment.GetEnvironmentVariable("PHOTOFLOW_SCREENSHOTS");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
            frame!.Save(Path.Combine(folder, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
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
    }
}
