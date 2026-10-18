// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.Core.Catalog;
using PhotoFlow.App.Views;
using PhotoFlow.Core.Paths;
using Serilog;

namespace PhotoFlow.App;

public sealed class App : Application
{
    private readonly ThemeVariant? _startupTheme;
    private ServiceProvider? _services;
    private PhotoFlowHost? _host;

    public App()
    {
    }

    /// <param name="startupTheme">Theme requested on the command line (<c>--theme light|dark</c>); dark by default.</param>
    public App(ThemeVariant? startupTheme) => _startupTheme = startupTheme;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Applies a theme preference to the running application (board 5i: immediate, unlike the language).</summary>
    public static void ApplyTheme(ThemePreference theme)
    {
        if (Current is { } app)
        {
            app.RequestedThemeVariant = ToVariant(theme);
        }
    }

    private static ThemeVariant ToVariant(ThemePreference theme) => theme switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.System => ThemeVariant.Default,
        _ => ThemeVariant.Dark,
    };

    public override void OnFrameworkInitializationCompleted()
    {
        if (_startupTheme is not null)
        {
            RequestedThemeVariant = _startupTheme;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = ConfigureServices();
            ApplyStartupPreferences(_services.GetRequiredService<IAppPaths>());
            var logger = _services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("PhotoFlow starting, version {Version}", typeof(App).Assembly.GetName().Version);

            var window = new MainWindow { DataContext = new MainWindowViewModel() };
            desktop.MainWindow = window;
            desktop.Exit += (_, _) =>
            {
                (window.DataContext as IDisposable)?.Dispose();
                _host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _services.Dispose();
            };
            _ = StartHostAsync(window, (DefaultAppPaths)_services.GetRequiredService<IAppPaths>(), logger);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Language (P18: read once, before the first window) and theme from the settings; <c>--theme</c> wins.</summary>
    private void ApplyStartupPreferences(IAppPaths paths)
    {
        var preferences = SettingsStore.LoadStartupPreferences(paths);
        if (preferences.Language is { } language)
        {
            var culture = CultureInfo.GetCultureInfo(language);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        if (_startupTheme is null)
        {
            RequestedThemeVariant = ToVariant(preferences.Theme);
        }
    }

    /// <summary>
    /// Opens catalog, exiftool and the other services off the UI thread, then shows the real pages. At the very first
    /// start the folder of the catalog is asked first (spec §9); a move or another catalog chosen in the settings is
    /// applied before the catalog opens.
    /// </summary>
    private async Task StartHostAsync(MainWindow window, DefaultAppPaths defaults, Microsoft.Extensions.Logging.ILogger logger)
    {
        var location = new CatalogLocationService(defaults);
        if (location.NeedsFirstChoice)
        {
            ((MainWindowViewModel)window.DataContext!).ShowPage(new CatalogLocationViewModel(location, () => StartHostAsync(window, defaults, logger)));
            return;
        }

        IAppPaths paths = defaults;
        try
        {
            await Task.Run(() => location.ApplyPendingAsync(CancellationToken.None));
            paths = location.Resolve();
            _host = await Task.Run(() => PhotoFlowHost.StartAsync(paths, CancellationToken.None));
            _host.CatalogLocation = location;
            if (_host.Settings.CheckUpdates)
            {
                _ = CheckUpdatesAsync(_host, logger);
            }
            var viewModel = new MainWindowViewModel(_host);
            viewModel.RefreshStatus();
            window.DataContext = viewModel;
            await viewModel.CurrentPage.ActivateAsync();
        }
        catch (CatalogLockedException e)
        {
            logger.LogWarning(e, "Catalog in use by another window");
            ((MainWindowViewModel)window.DataContext!).StatusText = Strings.Get("Status.CatalogLocked");
        }
        catch (CatalogCorruptException e)
        {
            logger.LogError(e, "Catalog damaged");
            var shell = (MainWindowViewModel)window.DataContext!;
            shell.StatusText = Strings.Format("Status.StartupFailed", e.Message);
            shell.ShowPage(new CatalogProblemViewModel(e.Message, new CatalogRestoreService(paths), () => StartHostAsync(window, defaults, logger)));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            logger.LogError(e, "Startup failed");
            ((MainWindowViewModel)window.DataContext!).StatusText = Strings.Format("Status.StartupFailed", e.Message);
        }
    }

    /// <summary>In the background: a newer version is announced in the status bar (installed only from the settings).</summary>
    private async Task CheckUpdatesAsync(PhotoFlowHost host, Microsoft.Extensions.Logging.ILogger logger)
    {
        try
        {
            if (await Task.Run(host.Updates.CheckAsync) is { } version)
            {
                logger.LogInformation("Update available: {Version}", version);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.DataContext: MainWindowViewModel shell })
                    {
                        shell.RefreshStatus();
                    }
                });
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogWarning(e, "Update check failed"); // offline: nothing to say
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        IAppPaths paths = new DefaultAppPaths();
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "photoflow-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddLogging(builder => builder.AddSerilog(serilog, dispose: true));
        return services.BuildServiceProvider();
    }
}
