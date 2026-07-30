using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
#if UI_CAPTURE
using Avalonia.Media.Imaging;
#endif
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FoxTrans.Desktop.Diagnostics;
using FoxTrans.Desktop.ViewModels;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace FoxTrans.Desktop.Views;

public sealed partial class MainWindow : Window
{
    internal static readonly TimeSpan IdlePollInterval =
        TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan ActiveVisualFrameInterval =
        TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30);
    internal static readonly TimeSpan ProcessingVisualFrameInterval =
        TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 8);
    /// <summary>
    /// Last-resort bound on closing. It sits above the view model's runtime stop
    /// bound so the inner timeout always wins and this one only ever covers a
    /// shutdown that hangs outside the runtime entirely.
    /// </summary>
    internal static readonly TimeSpan ShutdownBound =
        MainWindowViewModel.RuntimeStopBound + TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _idleClock;
    private readonly DispatcherTimer _visualClock;
    private readonly Stopwatch _animationTime = new();
    private readonly Border _windowSurface;
    private readonly Grid _drawerLayer;
    private readonly Border _settingsDrawer;
    private readonly ContentControl _settingsHost;
    private readonly ContentControl _liveHost;
    private readonly TranslateTransform _drawerTransform;
    private readonly ShapePath _maximizeGlyph;
    private readonly ShapePath _restoreGlyph;
    private bool _allowClose;
    private bool _closing;
    private bool _drawerFrameQueued;
    private bool _visualClockRunning;
    private double _drawerProgress;
    private double _drawerTarget;
    private TimeSpan _lastAnimationElapsed;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _windowSurface = this.FindControl<Border>("WindowSurface")!;
        _drawerLayer = this.FindControl<Grid>("DrawerLayer")!;
        _settingsDrawer = this.FindControl<Border>("SettingsDrawer")!;
        _settingsHost = this.FindControl<ContentControl>("SettingsHost")!;
        _liveHost = this.FindControl<ContentControl>("LiveHost")!;
        _drawerTransform =
            (TranslateTransform)_settingsDrawer.RenderTransform!;
        _maximizeGlyph = this.FindControl<ShapePath>("MaximizeGlyph")!;
        _restoreGlyph = this.FindControl<ShapePath>("RestoreGlyph")!;

        _idleClock = new DispatcherTimer
        {
            Interval = IdlePollInterval
        };
        _idleClock.Tick += OnIdleTick;
        _visualClock = new DispatcherTimer
        {
            Interval = ActiveVisualFrameInterval
        };
        _visualClock.Tick += OnVisualTick;
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
        RestorePlacement();
        if (!viewModel.IsInitializing)
            EnsureLiveContent();
        if (viewModel.IsSettingsOpen)
            EnsureSettingsContent();
        StartupTrace.Mark("window-ctor");
    }

    public MainWindowViewModel ViewModel =>
        (MainWindowViewModel)DataContext!;
    internal TimeSpan AnimationInterval => _visualClockRunning
        ? _visualClock.Interval
        : _idleClock.Interval;

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        StartupTrace.Mark("opened");
        _ = TraceFirstFrameAsync();
#if UI_CAPTURE
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
#endif
        UpdateDrawerWidth();
        UpdateMaximizeGlyph();
        _animationTime.Start();
        ViewModel.Tick(DateTimeOffset.UtcNow, 0, advanceVisuals: false);
        _idleClock.Start();
        await ViewModel.InitializeAsync();
        StartupTrace.Mark("initialized");
        UpdateVisualScheduling();
        Dispatcher.UIThread.Post(
            ViewModel.StartVrOverlayHost,
            DispatcherPriority.Background);
#if UI_CAPTURE
        ViewModel.ApplyDesignPreview(ViewModel.RequestedDesignPreview);
        if (ViewModel.RequestedCapturePath is { } capturePath)
            await CapturePreviewAndCloseAsync(capturePath);
#endif
    }

    private async Task TraceFirstFrameAsync()
    {
        if (!StartupTrace.Enabled)
            return;

        try
        {
            if (ElementComposition.GetElementVisual(this)?.Compositor is not
                { } compositor)
            {
                TraceFirstFrameFallback();
                return;
            }

            var batch =
                compositor.RequestCompositionBatchCommitAsync();
            await batch.Rendered;
            StartupTrace.Mark("first-frame");
        }
        catch
        {
            TraceFirstFrameFallback();
        }
    }

    private static void TraceFirstFrameFallback() =>
        Dispatcher.UIThread.Post(
            () => StartupTrace.Mark("first-frame"),
            DispatcherPriority.Loaded);

#if UI_CAPTURE
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
#endif

    private void OnIdleTick(object? sender, EventArgs eventArgs)
    {
        if (_visualClockRunning || IsDrawerAnimating)
            return;

        TickVisualState(advanceVisuals: false);
        UpdateVisualScheduling();
    }

    private void OnVisualTick(object? sender, EventArgs eventArgs)
    {
        if (!CanScheduleVisualFrames())
        {
            StopVisualClock();
            return;
        }

        TickVisualState(advanceVisuals: true);
        UpdateVisualScheduling();
    }

    private void OnDrawerFrame(TimeSpan timestamp)
    {
        _drawerFrameQueued = false;
        if (!CanScheduleVisualFrames() || !IsDrawerAnimating)
            return;

        AdvanceDrawer(ConsumeAnimationDelta());
        UpdateVisualScheduling();
    }

    private void TickVisualState(bool advanceVisuals)
    {
        TimeSpan elapsed = _animationTime.Elapsed;
        double delta = ConsumeAnimationDelta(elapsed);
        if (advanceVisuals)
            AdvanceDrawer(delta);

        if (!IsVisible || !IsActive ||
            WindowState == WindowState.Minimized)
            return;
        ViewModel.Tick(
            DateTimeOffset.UtcNow,
            ViewModel.Settings.ReducedMotion
                ? 0
                : elapsed.TotalSeconds,
            advanceVisuals);
    }

    private double ConsumeAnimationDelta() =>
        ConsumeAnimationDelta(_animationTime.Elapsed);

    private double ConsumeAnimationDelta(TimeSpan elapsed)
    {
        double delta = Math.Clamp(
            (elapsed - _lastAnimationElapsed).TotalSeconds,
            0,
            0.1);
        _lastAnimationElapsed = elapsed;
        return delta;
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
            {
                EnsureLiveContent();
                UpdateVisualScheduling();
            }
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
        }
        UpdateVisualScheduling();
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
    }

    private bool IsDrawerAnimating =>
        Math.Abs(_drawerProgress - _drawerTarget) >= 0.0001;

    private bool CanScheduleVisualFrames() =>
        IsVisible &&
        IsActive &&
        WindowState != WindowState.Minimized;

    private void UpdateVisualScheduling()
    {
        if (!CanScheduleVisualFrames())
        {
            StopVisualClock();
            return;
        }

        if (IsDrawerAnimating)
        {
            StopVisualClock();
            RequestDrawerFrame();
            return;
        }

        TimeSpan? interval = ViewModel.GetVisualTickMode(
            DateTimeOffset.UtcNow) switch
        {
            VisualTickMode.Active => ActiveVisualFrameInterval,
            VisualTickMode.Processing => ProcessingVisualFrameInterval,
            _ => null
        };
        if (interval is null)
        {
            StopVisualClock();
            return;
        }

        _visualClock.Interval = interval.Value;
        if (_visualClockRunning)
            return;

        _visualClock.Start();
        _visualClockRunning = true;
    }

    private void RequestDrawerFrame()
    {
        if (_drawerFrameQueued)
            return;

        _drawerFrameQueued = true;
        RequestAnimationFrame(OnDrawerFrame);
    }

    private void StopVisualClock()
    {
        if (!_visualClockRunning)
            return;

        _visualClock.Stop();
        _visualClockRunning = false;
    }

    private void OnActivated(object? sender, EventArgs eventArgs)
    {
        TickVisualState(advanceVisuals: false);
        UpdateVisualScheduling();
    }

    private void OnDeactivated(object? sender, EventArgs eventArgs)
    {
        StopVisualClock();
    }

    private async void OnClosing(
        object? sender,
        WindowClosingEventArgs eventArgs)
    {
        if (_allowClose)
            return;
        eventArgs.Cancel = true;
        if (_closing)
            return;

        _closing = true;
        _idleClock.Stop();
        StopVisualClock();
        try
        {
            SavePlacement();
        }
        catch
        {
            // Window placement is a preference, never a reason to stay open.
        }

        // Shutdown is bounded and its outcome never decides whether the window
        // closes: a session that ignores cancellation must not leave a window
        // that cannot be closed and no longer ticks.
        Task shutdown = ObserveShutdownAsync();
        await Task.WhenAny(shutdown, Task.Delay(ShutdownBound));
        _allowClose = true;
        Close();
    }

    private async Task ObserveShutdownAsync()
    {
        try
        {
            await ViewModel.ShutdownAsync();
        }
        catch
        {
            // A shutdown failure is already reported through the runtime
            // reporter; it cannot block the closing window.
        }
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _idleClock.Stop();
        StopVisualClock();
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
        {
            UpdateMaximizeGlyph();
            UpdateVisualScheduling();
        }
        if (eventArgs.Property == IsVisibleProperty)
        {
            if (IsVisible && WindowState != WindowState.Minimized)
            {
                _idleClock.Start();
                UpdateVisualScheduling();
            }
            else
            {
                _idleClock.Stop();
                StopVisualClock();
            }
        }
    }

    private void UpdateMaximizeGlyph()
    {
        bool maximized = WindowState == WindowState.Maximized;
        _windowSurface.CornerRadius = maximized
            ? new CornerRadius(0)
            : new CornerRadius(14);
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
            WindowStartupLocation = WindowStartupLocation.Manual;
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
