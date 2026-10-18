// SPDX-License-Identifier: Apache-2.0

using Avalonia;
using Avalonia.Styling;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Paths;
using Velopack;

namespace PhotoFlow.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack first (spec §11): the install, update and uninstall steps run here and exit. At uninstall the caches
        // go, the user's data stays.
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            velopack = velopack.OnBeforeUninstallFastCallback(_ => UninstallCleanup.Run(new DefaultAppPaths()));
        }

        velopack.Run();
        return BuildAvaloniaApp(ParseTheme(args)).StartWithClassicDesktopLifetime(args);
    }

    // Parameterless overload used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static AppBuilder BuildAvaloniaApp(ThemeVariant? theme) =>
        AppBuilder.Configure(() => new App(theme))
            .UsePlatformDetect()
            .LogToTrace();

    private static ThemeVariant? ParseTheme(string[] args)
    {
        var index = Array.IndexOf(args, "--theme");
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        return args[index + 1].ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => null,
        };
    }
}
