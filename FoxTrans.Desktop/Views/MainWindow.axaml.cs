using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FoxTrans.Desktop.ViewModels;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace FoxTrans.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _animationClock;
    private readonly Stopwatch _animationTime = new();
    private readonly Grid _drawerLayer;
    private readonly Border _settingsDrawer;
    private readonly ContentControl _settingsHost;
    private readonly ContentControl _liveHost;
    private readonly Grid _startupShell;
    private readonly Ellipse _startupOrb;
    private readonly ScaleTransform _startupOrbScale;
    private readonly TranslateTransform _drawerTransform;
    private readonly ShapePath _maximizeGlyph;
    private readonly ShapePath _restoreGlyph;
    private bool _allowClose;
    private double _drawerProgress;
    private double _drawerTarget;
    private TimeSpan _lastAnimationElapsed;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _drawerLayer = this.FindControl<Grid>("DrawerLayer")!;
        _settingsDrawer = this.FindControl<Border>("SettingsDrawer")!;
        _settingsHost = this.FindControl<ContentControl>("SettingsHost")!;
        _liveHost = this.FindControl<ContentControl>("LiveHost")!;
        _startupShell = this.FindControl<Grid>("StartupShell")!;
        _startupOrb = this.FindControl<Ellipse>("StartupOrb")!;
        _startupOrbScale = (ScaleTransform)_startupOrb.RenderTransform!;
        _drawerTransform =
            (TranslateTransform)_settingsDrawer.RenderTransform!;
        _maximizeGlyph = this.FindControl<ShapePath>("MaximizeGlyph")!;
        _restoreGlyph = this.FindControl<ShapePath>("RestoreGlyph")!;

        _animationClock = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _animationClock.Tick += OnAnimationTick;
        Opened += OnOpened;
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        Closing += OnClosing;
        Closed += OnClosed;
        SizeChanged += (_, _) => UpdateDrawerWidth();
        KeyDown += OnKeyDown;
        PropertyChanged += OnWindowPropertyChanged;
    }

    public MainWindow(MainWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Settings.CopyDiagnosticsRequested +=
            OnCopyDiagnosticsRequested;
        if (!viewModel.IsInitializing)
            EnsureLiveContent();
        if (viewModel.IsSettingsOpen)
            EnsureSettingsContent();
    }

    public MainWindowViewModel ViewModel =>
        (MainWindowViewModel)DataContext!;

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        RestorePlacement();
        if (ViewModel.RequestedDesignPreview is { } preview)
        {
            Width = string.Equals(
                preview,
                "compact",
                StringComparison.OrdinalIgnoreCase)
                ? 900
                : 1180;
            Height = string.Equals(
                preview,
                "compact",
                StringComparison.OrdinalIgnoreCase)
                ? 620
                : 760;
        }
        UpdateDrawerWidth();
        UpdateMaximizeGlyph();
        _animationTime.Start();
        _animationClock.Start();
        ViewModel.Tick(DateTimeOffset.UtcNow, 0);
        await ViewModel.InitializeAsync();
        ViewModel.ApplyDesignPreview(ViewModel.RequestedDesignPreview);
        if (ViewModel.RequestedCapturePath is { } capturePath)
            await CapturePreviewAndCloseAsync(capturePath);
    }

    private async Task CapturePreviewAndCloseAsync(string capturePath)
    {
        bool captureSuccessBloom = string.Equals(
            ViewModel.RequestedDesignPreview,
            "success",
            StringComparison.OrdinalIgnoreCase);
        await Task.Delay(captureSuccessBloom ? 320 : 1100);
        ViewModel.Tick(
            DateTimeOffset.UtcNow,
            ViewModel.Settings.ReducedMotion
                ? 0
                : _animationTime.Elapsed.TotalSeconds);
        if (ViewModel.IsSettingsOpen)
        {
            _drawerTarget = 1;
            _drawerProgress = 1;
            _drawerLayer.IsVisible = true;
            _drawerLayer.Opacity = 1;
            _drawerTransform.X = 0;
        }
        await Dispatcher.UIThread.InvokeAsync(
            UpdateLayout,
            DispatcherPriority.Render);
        var bitmap = new RenderTargetBitmap(
            new PixelSize(
                Math.Max(1, (int)Math.Round(ClientSize.Width)),
                Math.Max(1, (int)Math.Round(ClientSize.Height))),
            new Vector(96, 96));
        bitmap.Render(this);
        string fullPath = Path.GetFullPath(capturePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        bitmap.Save(fullPath, PngBitmapEncoderOptions.Default);
        bitmap.Dispose();
        Close();
    }

    private void OnAnimationTick(object? sender, EventArgs eventArgs)
    {
        TimeSpan elapsed = _animationTime.Elapsed;
        double delta = Math.Clamp(
            (elapsed - _lastAnimationElapsed).TotalSeconds,
            0,
            0.1);
        _lastAnimationElapsed = elapsed;
        AdvanceDrawer(delta);
        AdvanceStartupShell(elapsed.TotalSeconds);

        if (!IsVisible || WindowState == WindowState.Minimized)
            return;
        ViewModel.Tick(
            DateTimeOffset.UtcNow,
            ViewModel.Settings.ReducedMotion
                ? 0
                : elapsed.TotalSeconds);
    }

    private void AdvanceDrawer(double deltaSeconds)
    {
        if (Math.Abs(_drawerProgress - _drawerTarget) < 0.0001)
            return;

        bool opening = _drawerTarget > _drawerProgress;
        double duration = ViewModel.Settings.ReducedMotion
            ? 0.09
            : opening ? 0.28 : 0.22;
        double direction = opening ? 1 : -1;
        _drawerProgress = Math.Clamp(
            _drawerProgress + direction * deltaSeconds / duration,
            0,
            1);
        double eased = 1 - Math.Pow(1 - _drawerProgress, 3);
        _drawerLayer.Opacity = eased;
        _drawerTransform.X = ViewModel.Settings.ReducedMotion
            ? 0
            : _settingsDrawer.Width * (1 - eased);
        if (_drawerProgress <= 0 && _drawerTarget <= 0)
            _drawerLayer.IsVisible = false;
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName ==
            nameof(MainWindowViewModel.IsInitializing))
        {
            if (!ViewModel.IsInitializing)
                EnsureLiveContent();
            return;
        }

        if (eventArgs.PropertyName !=
            nameof(MainWindowViewModel.IsSettingsOpen))
        {
            return;
        }

        _drawerTarget = ViewModel.IsSettingsOpen ? 1 : 0;
        if (_drawerTarget > 0)
        {
            EnsureSettingsContent();
            _drawerLayer.IsVisible = true;
            _animationClock.Start();
        }
    }

    private void EnsureSettingsContent()
    {
        if (_settingsHost.Content is null)
            _settingsHost.Content = new SettingsView();
    }

    private void EnsureLiveContent()
    {
        if (_liveHost.Content is null)
            _liveHost.Content = new LiveStudioView();
        _startupShell.IsVisible = false;
    }

    private void AdvanceStartupShell(double animationSeconds)
    {
        if (!_startupShell.IsVisible)
            return;
        double wave = 0.5 + 0.5 * Math.Sin(animationSeconds * 1.7);
        double scale = 0.965 + wave * 0.045;
        _startupOrbScale.ScaleX = scale;
        _startupOrbScale.ScaleY = scale;
        _startupOrb.Opacity = 0.30 + wave * 0.12;
    }

    private void OnActivated(object? sender, EventArgs eventArgs)
    {
        _animationClock.Interval = TimeSpan.FromMilliseconds(16);
        if (IsVisible)
            _animationClock.Start();
    }

    private void OnDeactivated(object? sender, EventArgs eventArgs)
    {
        _animationClock.Interval = TimeSpan.FromMilliseconds(100);
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
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Settings.CopyDiagnosticsRequested -=
            OnCopyDiagnosticsRequested;
    }

    private void OnTitleBarPointerPressed(
        object? sender,
        PointerPressedEventArgs eventArgs)
    {
        PointerPoint point = eventArgs.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed ||
            IsButtonSource(eventArgs.Source))
        {
            return;
        }

        if (eventArgs.ClickCount == 2)
        {
            ToggleMaximize();
            eventArgs.Handled = true;
            return;
        }

        BeginMoveDrag(eventArgs);
        eventArgs.Handled = true;
    }

    private static bool IsButtonSource(object? source)
    {
        if (source is Button)
            return true;
        return source is Visual visual &&
            visual.GetVisualAncestors().OfType<Button>().Any();
    }

    private void OnMinimizeClick(
        object? sender,
        RoutedEventArgs eventArgs) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(
        object? sender,
        RoutedEventArgs eventArgs) =>
        ToggleMaximize();

    private void OnCloseClick(
        object? sender,
        RoutedEventArgs eventArgs) =>
        Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void OnWindowPropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.Property == WindowStateProperty)
            UpdateMaximizeGlyph();
        if (eventArgs.Property == IsVisibleProperty)
        {
            if (IsVisible && WindowState != WindowState.Minimized)
                _animationClock.Start();
            else
                _animationClock.Stop();
        }
    }

    private void UpdateMaximizeGlyph()
    {
        bool maximized = WindowState == WindowState.Maximized;
        _maximizeGlyph.IsVisible = !maximized;
        _restoreGlyph.IsVisible = maximized;
        if (this.FindControl<Button>("MaximizeButton") is { } button)
            ToolTip.SetTip(button, maximized ? "Restore" : "Maximize");
    }

    private void OnBackdropPointerPressed(
        object? sender,
        PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ViewModel.TryCloseSettingsFromBackdrop();
            eventArgs.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape && ViewModel.IsSettingsOpen)
        {
            ViewModel.Settings.RequestClose();
            eventArgs.Handled = true;
        }
    }

    private async void OnCopyDiagnosticsRequested(string diagnostics)
    {
        try
        {
            if (Clipboard is not null)
            {
                await Clipboard.SetValueAsync(
                    DataFormat.Text,
                    diagnostics);
                ViewModel.Settings.SetFeedback(
                    "Sanitized diagnostics copied");
            }
        }
        catch
        {
            ViewModel.Settings.SetFeedback(
                "Diagnostics could not be copied.",
                true);
        }
    }

    private void UpdateDrawerWidth()
    {
        double width = Math.Min(590, Math.Max(0, Bounds.Width * 0.92));
        if (Bounds.Width >= 1040)
            width = Math.Min(570, Bounds.Width * 0.54);
        _settingsDrawer.Width = width;
        if (_drawerProgress <= 0)
            _drawerTransform.X = width;
    }

    private void OnResizeWest(
        object? sender,
        PointerPressedEventArgs eventArgs) =>
        BeginResize(WindowEdge.West, eventArgs);

    private void OnResizeEast(
        object? sender,
        PointerPressedEventArgs eventArgs) =>
        BeginResize(WindowEdge.East, eventArgs);

    private void OnResizeNorth(
        object? sender,
        PointerPressedEventArgs eventArgs) =>
        BeginResize(WindowEdge.North, eventArgs);

    private void OnResizeSouth(
        object? sender,
        PointerPressedEventArgs eventArgs) =>
        BeginResize(WindowEdge.South, eventArgs);

    private void OnResizeNorthWest(
        object? sender,
        PointerPressedEventArgs eventArgs) =>
        BeginResize(WindowEdge.NorthWest, eventArgs);

    private void OnResizeNorthEast(
        object? sender,
        PointerPressedEventArgs eventArgs) =>
        BeginResize(WindowEdge.NorthEast, eventArgs);

    private void OnResizeSouthWest(
        object? sender,
        PointerPressedEventArgs eventArgs) =>
        BeginResize(WindowEdge.SouthWest, eventArgs);

    private void OnResizeSouthEast(
        object? sender,
        PointerPressedEventArgs eventArgs) =>
        BeginResize(WindowEdge.SouthEast, eventArgs);

    private void BeginResize(
        WindowEdge edge,
        PointerPressedEventArgs eventArgs)
    {
        if (WindowState != WindowState.Normal ||
            !eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        BeginResizeDrag(edge, eventArgs);
        eventArgs.Handled = true;
    }

    private void RestorePlacement()
    {
        Services.DesktopPreferences preferences = ViewModel.Preferences;
        if (!preferences.RememberWindowPlacement)
            return;
        Width = preferences.WindowWidth;
        Height = preferences.WindowHeight;
        if (preferences.WindowX is int x &&
            preferences.WindowY is int y)
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
