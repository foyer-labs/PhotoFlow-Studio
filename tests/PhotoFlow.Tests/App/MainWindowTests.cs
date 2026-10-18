// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void Shell_has_the_board_04_structure()
    {
        var window = Show(1440);
        var navigation = window.FindControl<Border>("Navigation")!;
        Assert.Equal(184, navigation.Bounds.Width);
        Assert.Equal(32, window.FindControl<Grid>("TitleBar")!.Bounds.Height);
        window.Close();
    }

    [AvaloniaFact]
    public void Navigation_is_compact_at_1280()
    {
        var window = Show(1280);
        Assert.Equal(48, window.FindControl<Border>("Navigation")!.Bounds.Width);
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_digit_switches_section_and_ctrl_k_focuses_search()
    {
        var window = Show(1440);
        var viewModel = (MainWindowViewModel)window.DataContext!;

        window.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.Control);
        Assert.Equal(SectionCatalog.Review, viewModel.SelectedSection.Id);

        window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control);
        Assert.True(window.FindControl<TextBox>("SearchBox")!.IsFocused);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("dark")]
    [InlineData("light")]
    public void Shell_renders_in_both_themes(string theme)
    {
        var window = Show(1440);
        window.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        // Optional visual check: set PHOTOFLOW_SCREENSHOTS to a folder outside the repository.
        var folder = Environment.GetEnvironmentVariable("PHOTOFLOW_SCREENSHOTS");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
            frame!.Save(Path.Combine(folder, $"shell-{theme}.png"), new PngBitmapEncoderOptions());
        }

        window.Close();
    }

    private static MainWindow Show(double width)
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel(), Width = width, Height = 900 };
        window.Show();
        return window;
    }
}
