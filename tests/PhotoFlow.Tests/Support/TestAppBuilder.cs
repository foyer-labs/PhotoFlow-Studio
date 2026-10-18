// SPDX-License-Identifier: Apache-2.0

using Avalonia;
using Avalonia.Headless;
using PhotoFlow.Tests.Support;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// One Application and Dispatcher for the whole assembly: recreating them per test races on the Skia render loop.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace PhotoFlow.Tests.Support;

/// <summary>
/// All headless Avalonia test classes belong to this collection: the shared Application is initialized lazily
/// and two classes starting in parallel would race on it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AvaloniaCollection
{
    public const string Name = "Avalonia";
}

/// <summary>Headless Avalonia with real Skia rendering and text shaping (frames can be captured).</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<PhotoFlow.App.App>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
