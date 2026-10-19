// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Input;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

public sealed partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.WindowWidth = e.NewSize.Width;
            }
        };
        TitleBar.PointerPressed += OnTitleBarPointerPressed;
        TitleBar.DoubleTapped += (_, _) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.SearchFocusRequested -= OnSearchFocusRequested;
            _viewModel.FullScreenRequested -= OnFullScreenRequested;
        }

        _viewModel = DataContext as MainWindowViewModel;
        KeyBindings.Clear();
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.SearchFocusRequested += OnSearchFocusRequested;
        _viewModel.FullScreenRequested += OnFullScreenRequested;
        _viewModel.WindowWidth = Bounds.Width > 0 ? Bounds.Width : Width;
        foreach (var binding in _viewModel.Commands.CreateKeyBindings(_viewModel.ResolveCommand))
        {
            KeyBindings.Add(binding);
        }
    }

    private void OnSearchFocusRequested(object? sender, EventArgs e) => SearchBox.Focus();

    private WindowState _beforeFullScreen = WindowState.Maximized;

    private void OnFullScreenRequested(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _beforeFullScreen;
        }
        else
        {
            _beforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == TitleBar && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
        {
            BeginMoveDrag(e);
        }
    }
}
