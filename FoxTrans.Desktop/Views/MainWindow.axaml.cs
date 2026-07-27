using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FoxTrans.Desktop.ViewModels;

namespace FoxTrans.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _animationClock;
    private readonly Stopwatch _animationTime = new();
    private bool _allowClose;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _animationClock = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _animationClock.Tick += OnAnimationTick;
        Opened += OnOpened;
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public MainWindow(MainWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    public MainWindowViewModel ViewModel =>
        (MainWindowViewModel)DataContext!;

    private void OnOpened(object? sender, EventArgs eventArgs)
    {
        RestorePlacement();
        _animationTime.Start();
        _animationClock.Start();
        ViewModel.Tick(DateTimeOffset.UtcNow, 0);
    }

    private void OnAnimationTick(object? sender, EventArgs eventArgs)
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
            return;
        ViewModel.Tick(
            DateTimeOffset.UtcNow,
            ViewModel.Settings.ReducedMotion
                ? 0
                : _animationTime.Elapsed.TotalSeconds);
    }

    private void OnActivated(object? sender, EventArgs eventArgs)
    {
        _animationClock.Interval = TimeSpan.FromMilliseconds(33);
        if (IsVisible)
            _animationClock.Start();
    }

    private void OnDeactivated(object? sender, EventArgs eventArgs)
    {
        _animationClock.Interval = TimeSpan.FromMilliseconds(250);
    }

    private async void OnClosing(
        object? sender,
        WindowClosingEventArgs eventArgs)
    {
        if (_allowClose)
            return;
        eventArgs.Cancel = true;
        _animationClock.Stop();
        SavePlacement();
        await ViewModel.ShutdownAsync();
        _allowClose = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _animationClock.Stop();
        _animationTime.Stop();
    }

    private void RestorePlacement()
    {
        Services.DesktopPreferences preferences = ViewModel.Preferences;
        if (!preferences.RememberWindowPlacement)
            return;
        Width = preferences.WindowWidth;
        Height = preferences.WindowHeight;
        if (preferences.WindowX is int x && preferences.WindowY is int y)
        {
            Position = new PixelPoint(x, y);
        }
    }

    private void SavePlacement()
    {
        Services.DesktopPreferences preferences = ViewModel.Preferences;
        if (!preferences.RememberWindowPlacement)
        {
            ViewModel.UpdateWindowPreferences(1180, 760, null, null);
            return;
        }
        if (WindowState != WindowState.Normal)
            return;
        ViewModel.UpdateWindowPreferences(
            Math.Max(MinWidth, Width),
            Math.Max(MinHeight, Height),
            Position.X,
            Position.Y);
    }
}
