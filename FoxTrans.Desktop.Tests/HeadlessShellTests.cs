using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using FoxTrans.Desktop;
using FoxTrans.Desktop.Controls;
using FoxTrans.Desktop.Models;
using FoxTrans.Desktop.Services;
using FoxTrans.Desktop.ViewModels;
using FoxTrans.Desktop.Views;
using Xunit;

public sealed class HeadlessShellTests
{
    [Fact]
    public void VisualCadencesStayWithinTheGpuBudget()
    {
        Assert.Equal(
            TimeSpan.TicksPerSecond / 30,
            MainWindow.ActiveVisualFrameInterval.Ticks);
        Assert.Equal(
            TimeSpan.TicksPerSecond / 8,
            MainWindow.ProcessingVisualFrameInterval.Ticks);
        Assert.Equal(
            TimeSpan.FromMilliseconds(100),
            MainWindow.IdlePollInterval);
    }

    [AvaloniaFact]
    public async Task DeferredStartupKeepsPreWindowConstructionLightweight()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "foxtrans-desktop-startup-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var devices = new CountingDevices();
        var store = new DesktopPreferencesStore(
            Path.Combine(directory, "desktop-preferences.json"));
        DesktopApplicationServices services =
            DesktopApplicationServices.Create(
                directory,
                store,
                new FoxTransRuntime(new BlockingSessionFactory()),
                devices,
                _ => "test-key",
                deferBootstrap: true);
        var viewModel = new MainWindowViewModel(services);
        var window = new MainWindow(viewModel);

        Assert.Equal(0, devices.EnumerationCount);
        Assert.True(viewModel.IsInitializing);
        Assert.Equal("Loading…", viewModel.PipelineName);
        Assert.Equal("Loading…", viewModel.PrimaryActionText);
        Assert.Empty(Descendants<SettingsView>(window));
        Assert.Null(Descendant<LiveStudioView>(window));
        Assert.Null(Descendant<VoiceWaveformControl>(window));
        Assert.Equal("Loading", viewModel.Live.StatusText);
        Assert.Equal("Preparing configuration", viewModel.Live.StatusDetail);

        await viewModel.ShutdownAsync();
        try
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [AvaloniaFact]
    public async Task SavedPlacementIsAppliedBeforeWindowOpens()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "foxtrans-desktop-placement-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var store = new DesktopPreferencesStore(
            Path.Combine(directory, "desktop-preferences.json"));
        store.Save(new(
            RememberWindowPlacement: true,
            WindowWidth: 1375,
            WindowHeight: 845,
            WindowX: 123,
            WindowY: 87));
        DesktopApplicationServices services =
            DesktopApplicationServices.Create(
                directory,
                store,
                new FoxTransRuntime(new BlockingSessionFactory()),
                new Devices(),
                _ => "test-key");
        var viewModel = new MainWindowViewModel(services);
        var window = new MainWindow(viewModel);

        Assert.False(window.IsVisible);
        Assert.Equal(1375, window.Width);
        Assert.Equal(845, window.Height);
        Assert.Equal(new PixelPoint(123, 87), window.Position);
        Assert.Equal(
            WindowStartupLocation.Manual,
            window.WindowStartupLocation);

        await viewModel.ShutdownAsync();
        try
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [AvaloniaTheory]
    [InlineData(1440, 900)]
    [InlineData(1180, 760)]
    [InlineData(900, 620)]
    public async Task SingleLiveShellRendersAtSupportedSizes(
        double width,
        double height)
    {
        await using TestDesktop desktop = TestDesktop.Create();
        MainWindow window = desktop.Window;
        window.Show();
        window.Width = width;
        window.Height = height;
        VoiceWaveformControl waveform =
            Descendant<VoiceWaveformControl>(window)!;

        using var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.True(window.ClientSize.Width >= width - 1);
        Assert.True(window.ClientSize.Height >= height - 1);
        Assert.NotNull(Descendant<LiveStudioView>(window));
        Assert.NotNull(waveform);
        Assert.InRange(waveform.Bounds.Width, 290, 420);
        Assert.Equal(76, waveform.Bounds.Height);
        Assert.Equal(
            104,
            Descendant<LiveStudioView>(window)!
                .FindControl<Border>("WaveformRail")!
                .Bounds.Height);
    }

    [AvaloniaFact]
    public async Task WindowUsesOnlyCustomChromeAndVectorControls()
    {
        await using TestDesktop desktop = TestDesktop.Create();
        MainWindow window = desktop.Window;
        window.Show();

        Assert.Equal(WindowDecorations.None, window.WindowDecorations);
        Assert.True(window.CanResize);
        Assert.Equal(900, window.MinWidth);
        Assert.Equal(620, window.MinHeight);
        Assert.Contains(
            WindowTransparencyLevel.Transparent,
            window.TransparencyLevelHint);
        Border windowSurface =
            window.FindControl<Border>("WindowSurface")!;
        Assert.Equal(new CornerRadius(14), windowSurface.CornerRadius);
        Assert.True(windowSurface.ClipToBounds);
        Assert.NotNull(window.FindControl<Grid>("TitleBar"));
        foreach (string name in new[]
                 {
                     "MinimizeButton",
                     "MaximizeButton",
                     "CloseButton"
                 })
        {
            Button control = window.FindControl<Button>(name)!;
            Assert.Equal(46, control.Width);
            Assert.Equal(46, control.Height);
            Assert.NotNull(Descendant<Avalonia.Controls.Shapes.Path>(control));
        }

        window.WindowState = WindowState.Maximized;
        Assert.Equal(new CornerRadius(0), windowSurface.CornerRadius);
        Assert.False(
            window.FindControl<Avalonia.Controls.Shapes.Path>(
                "MaximizeGlyph")!.IsVisible);
        Assert.True(
            window.FindControl<Avalonia.Controls.Shapes.Path>(
                "RestoreGlyph")!.IsVisible);
        window.WindowState = WindowState.Normal;
        Assert.Equal(new CornerRadius(14), windowSurface.CornerRadius);
        Assert.True(
            window.FindControl<Avalonia.Controls.Shapes.Path>(
                "MaximizeGlyph")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task HeaderAndPrimaryActionUseStablePolishedGeometry()
    {
        await using TestDesktop desktop = TestDesktop.Create();
        MainWindow window = desktop.Window;
        window.Show();
        window.UpdateLayout();

        StackPanel identity =
            window.FindControl<StackPanel>("HeaderIdentity")!;
        Assert.InRange(identity.Bounds.X, 20, 24);
        Assert.InRange(identity.Bounds.Y, 16, 20);

        Button action =
            window.FindControl<Button>("PrimaryActionButton")!;
        Assert.Equal(112, action.Bounds.Width);
        Assert.Equal(40, action.Bounds.Height);
        Assert.Equal(
            Avalonia.Layout.HorizontalAlignment.Center,
            action.HorizontalContentAlignment);
        Assert.Equal(
            Avalonia.Layout.VerticalAlignment.Center,
            action.VerticalContentAlignment);
    }

    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1180)]
    [InlineData(1440)]
    public async Task WindowControlsKeepTheirGeometryWithSafeRightInset(
        double width)
    {
        await using TestDesktop desktop = TestDesktop.Create();
        MainWindow window = desktop.Window;
        window.Width = width;
        window.Show();
        window.UpdateLayout();

        Grid titleBar = window.FindControl<Grid>("TitleBar")!;
        Button close = window.FindControl<Button>("CloseButton")!;
        Button maximize = window.FindControl<Button>("MaximizeButton")!;
        Button minimize = window.FindControl<Button>("MinimizeButton")!;

        Assert.InRange(
            titleBar.Bounds.Width - close.Bounds.Right,
            9.5,
            10.5);
        Assert.All(
            new[] { minimize, maximize, close },
            button =>
            {
                Assert.Equal(46, button.Bounds.Width);
                Assert.Equal(46, button.Bounds.Height);
                Assert.InRange(button.Bounds.Y, 14.5, 15.5);
            });
        Assert.Equal(maximize.Bounds.Right, close.Bounds.Left);
        Assert.Equal(minimize.Bounds.Right, maximize.Bounds.Left);
    }

    [AvaloniaFact]
    public async Task SettingsIsAnOverlayDrawerAndLiveRemainsPresent()
    {
        await using TestDesktop desktop = TestDesktop.Create();
        desktop.Window.Show();

        desktop.ViewModel.OpenSettingsCommand.Execute(null);

        Assert.True(desktop.ViewModel.IsSettingsOpen);
        Assert.NotNull(Descendant<SettingsView>(desktop.Window));
        Assert.NotNull(Descendant<LiveStudioView>(desktop.Window));
        Assert.True(desktop.ViewModel.TryCloseSettingsFromBackdrop());
        Assert.False(desktop.ViewModel.IsSettingsOpen);
    }

    [AvaloniaFact]
    public async Task SegmentsFitTheirContentAtMinimumWindowSize()
    {
        await using TestDesktop desktop = TestDesktop.Create();
        desktop.Window.Width = 900;
        desktop.Window.Height = 620;
        desktop.Window.Show();
        desktop.ViewModel.OpenSettingsCommand.Execute(null);
        desktop.ViewModel.Settings.SelectedSection =
            SettingsSection.Appearance;
        using var appearanceFrame = desktop.Window.CaptureRenderedFrame();
        IReadOnlyList<RadioButton> appearanceSegments =
            Descendants<RadioButton>(desktop.Window)
                .Where(button =>
                    button.Classes.Contains("segment") &&
                    button.Bounds.Width > 0)
                .ToArray();
        Assert.Contains(
            appearanceSegments,
            segment => Equals(segment.Content, "System"));
        Assert.InRange(
            appearanceSegments.Max(segment => segment.Bounds.Width) -
            appearanceSegments.Min(segment => segment.Bounds.Width),
            0,
            1);

        desktop.ViewModel.Settings.SelectedSection =
            SettingsSection.Providers;
        using var providerFrame = desktop.Window.CaptureRenderedFrame();
        IReadOnlyList<RadioButton> credentialSegments =
            Descendants<RadioButton>(desktop.Window)
                .Where(button =>
                    button.Classes.Contains("segment") &&
                    button.Bounds.Width > 0)
                .ToArray();

        Assert.NotEmpty(credentialSegments);
        Assert.All(
            appearanceSegments.Concat(credentialSegments),
            segment =>
        {
            Assert.Equal(38, segment.Bounds.Height);
            Assert.True(segment.DesiredSize.Width <= segment.Bounds.Width + 1);
            Assert.Equal(
                Avalonia.Layout.HorizontalAlignment.Center,
                segment.HorizontalContentAlignment);
            Assert.Equal(
                Avalonia.Layout.VerticalAlignment.Center,
                segment.VerticalContentAlignment);
        });
        Assert.Contains(
            credentialSegments,
            segment => Equals(segment.Content, "Environment variable"));
        Assert.Contains(
            credentialSegments,
            segment => Equals(segment.Content, "Inline value"));
    }

    [AvaloniaFact]
    public async Task StartStopCommandReflectsRuntimeStateAndDisposesSession()
    {
        var factory = new BlockingSessionFactory();
        await using TestDesktop desktop = TestDesktop.Create(
            runtime: new FoxTransRuntime(factory));
        desktop.Window.Show();

        await desktop.ViewModel.ToggleRuntimeCommand.ExecuteAsync(null);
        desktop.ViewModel.Tick(DateTimeOffset.UtcNow, 0.1);
        Assert.Equal(RuntimeState.Running, desktop.ViewModel.RuntimeState);
        Assert.Equal("Stop", desktop.ViewModel.PrimaryActionText);
        Assert.Equal(
            "Save & Restart",
            desktop.ViewModel.Settings.SaveActionText);

        await desktop.ViewModel.ToggleRuntimeCommand.ExecuteAsync(null);
        desktop.ViewModel.Tick(DateTimeOffset.UtcNow, 0.2);
        Assert.Equal(RuntimeState.Stopped, desktop.ViewModel.RuntimeState);
        Assert.Equal("Start", desktop.ViewModel.PrimaryActionText);
        Assert.Equal(1, factory.Session!.DisposeCount);
    }

    [AvaloniaFact]
    public async Task ShowingTheDesktopNeverStartsRuntimeAutomatically()
    {
        var factory = new BlockingSessionFactory();
        await using TestDesktop desktop = TestDesktop.Create(
            runtime: new FoxTransRuntime(factory));

        desktop.Window.Show();

        Assert.Equal(RuntimeState.Stopped, desktop.ViewModel.RuntimeState);
        Assert.Null(factory.Session);
        Assert.Equal("Start", desktop.ViewModel.PrimaryActionText);
    }

    [AvaloniaTheory]
    [InlineData(VoiceVisualizationMode.Idle)]
    [InlineData(VoiceVisualizationMode.Listening)]
    [InlineData(VoiceVisualizationMode.Speech)]
    [InlineData(VoiceVisualizationMode.Processing)]
    [InlineData(VoiceVisualizationMode.Success)]
    [InlineData(VoiceVisualizationMode.Error)]
    [InlineData(VoiceVisualizationMode.Stopping)]
    public void VoiceWaveformRendersEveryTypedMode(
        VoiceVisualizationMode mode)
    {
        var waveform = new VoiceWaveformControl
        {
            Width = 392,
            Height = 76,
            Mode = mode,
            AnimationSeconds = 1.5,
            AudioFrame = new(
                0.25f,
                0.5f,
                false,
                true,
                Enumerable.Repeat(0.35f, 12).ToArray(),
                1,
                DateTimeOffset.UnixEpoch)
        };
        var window = new Window
        {
            Width = 420,
            Height = 100,
            Content = waveform
        };
        window.Show();

        using var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.Equal((float)mode, waveform.CurrentState.StateValue);
        Assert.Equal(12, waveform.CurrentState.BarHeights.Length);
        window.Close();
    }

    [AvaloniaFact]
    public void VoiceWaveformNormalizesLevelsWithoutChangingOuterGeometry()
    {
        var waveform = new VoiceWaveformControl
        {
            Width = 392,
            Height = 76,
            Mode = VoiceVisualizationMode.Speech,
            AnimationSeconds = 0,
            AudioFrame = VisualFrame(0.01f, 0.02f)
        };
        var size = new Size(392, 76);
        waveform.Measure(size);
        waveform.Arrange(new Rect(size));
        using var frame = new RenderTargetBitmap(
            new PixelSize(392, 76),
            new Vector(96, 96));
        frame.Render(waveform);
        Rect quietBounds = waveform.Bounds;
        float quietAverage = Average(waveform.CurrentState.BarHeights.Span);

        waveform.AnimationSeconds = 0.5;
        waveform.AudioFrame = VisualFrame(0.72f, 0.96f);
        frame.Render(waveform);

        Assert.Equal(quietBounds, waveform.Bounds);
        float loudAverage = Average(waveform.CurrentState.BarHeights.Span);
        Assert.True(quietAverage > 0.55f);
        Assert.True(loudAverage > 0.55f);
        Assert.InRange(Math.Abs(quietAverage - loudAverage), 0, 0.15f);
    }

    [AvaloniaFact]
    public void VoiceWaveformExposesDedicatedStateColors()
    {
        Color success = Color.Parse("#51C98A");
        Color error = Color.Parse("#D85A68");
        var waveform = new VoiceWaveformControl
        {
            SuccessColor = success,
            ErrorColor = error
        };

        Assert.Equal(success, waveform.SuccessColor);
        Assert.Equal(error, waveform.ErrorColor);
    }

    [AvaloniaFact]
    public async Task AudioLlmCollapsesRecognitionWithoutLeavingAHole()
    {
        await using TestDesktop desktop = TestDesktop.Create();
        desktop.Window.Show();
        LiveStudioView live = Descendant<LiveStudioView>(desktop.Window)!;

        Assert.False(desktop.ViewModel.Live.HasRecognition);
        Assert.False(
            live.FindControl<StackPanel>("RecognitionSection")!.IsVisible);
        Assert.True(
            live.FindControl<StackPanel>("TranslationSection")!.IsVisible);
    }

    private static AudioVisualFrame VisualFrame(float rms, float peak) =>
        new(
            rms,
            peak,
            false,
            true,
            Enumerable.Repeat(rms, 12).ToArray(),
            1,
            DateTimeOffset.UnixEpoch);

    private static float Average(ReadOnlySpan<float> values)
    {
        float total = 0;
        foreach (float value in values)
            total += value;
        return values.IsEmpty ? 0 : total / values.Length;
    }

    [AvaloniaFact]
    public async Task InvalidConfigurationKeepsWindowUsableAndOpensEditor()
    {
        await using TestDesktop desktop = TestDesktop.Create(
            configText: "{ invalid json");
        desktop.Window.Show();

        Assert.False(desktop.ViewModel.HasValidPlan);
        Assert.True(desktop.ViewModel.IsSettingsOpen);
        Assert.True(desktop.ViewModel.Settings.FeedbackIsError);
        Assert.NotEmpty(desktop.ViewModel.Settings.Feedback);
        Assert.False(desktop.ViewModel.ToggleRuntimeCommand.CanExecute(null));
        Assert.NotNull(Descendant<SettingsView>(desktop.Window));
    }

    [AvaloniaFact]
    public async Task DarkAndLightThemesBothRender()
    {
        await using TestDesktop desktop = TestDesktop.Create();
        desktop.Window.Show();
        var app = (App)Application.Current!;
        app.ApplyAppearance(DesktopAppearance.Dark);
        using var dark = desktop.Window.CaptureRenderedFrame();
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        Assert.NotNull(dark);

        app.ApplyAppearance(DesktopAppearance.Light);
        using var light = desktop.Window.CaptureRenderedFrame();
        Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
        Assert.NotNull(light);
    }

    [AvaloniaFact]
    public async Task VisualTreeContainsNoResolvedSecret()
    {
        const string secret = "headless-environment-secret";
        await using TestDesktop desktop = TestDesktop.Create(secret: secret);
        desktop.Window.Show();

        string visibleText = string.Join(
            "\n",
            Descendants<TextBlock>(desktop.Window)
                .Select(block => block.Text ?? "")
                .Concat(
                    Descendants<TextBox>(desktop.Window)
                        .Select(box => box.Text ?? "")));

        Assert.DoesNotContain(secret, visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Authorization",
            visibleText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("full direct prompt", visibleText);
    }

    [Fact]
    public void ShellStopBoundsSitAboveTheRuntimeBudget()
    {
        // Each layer must be strictly larger than the one it contains, so the
        // innermost, best-informed timeout is always the one that fires and
        // reports. Equal or inverted bounds make the outer one useless.
        Assert.True(
            FoxTransRuntime.DefaultStopTimeout <
            MainWindowViewModel.RuntimeStopBound,
            "The shell must outlast the runtime stop it requests.");
        Assert.True(
            MainWindowViewModel.RuntimeStopBound < MainWindow.ShutdownBound,
            "Closing must outlast the shutdown it waits for.");
    }

    [AvaloniaFact]
    public async Task ShutdownCompletesWhenTheRuntimeRefusesToStop()
    {
        var runtime = new UnstoppableRuntime();
        await using TestDesktop desktop = TestDesktop.Create(runtime: runtime);
        desktop.Window.Show();
        runtime.SetState(RuntimeState.Running);

        // A stop that never succeeds must not propagate: the window's closing
        // sequence relies on shutdown always returning.
        await desktop.ViewModel.ShutdownAsync()
            .WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

        Assert.True(runtime.StopAttempted);
        Assert.True(runtime.IsDisposed);
    }

    [AvaloniaFact]
    public async Task ClosingSucceedsWhenTheRuntimeRefusesToStop()
    {
        var runtime = new UnstoppableRuntime();
        await using TestDesktop desktop = TestDesktop.Create(runtime: runtime);
        desktop.Window.Show();
        runtime.SetState(RuntimeState.Running);
        var closed = new TaskCompletionSource();
        desktop.Window.Closed += (_, _) => closed.TrySetResult();

        desktop.Window.Close();
        await WaitUntilAsync(() => closed.Task.IsCompleted);

        Assert.True(
            closed.Task.IsCompleted,
            "A runtime that refuses to stop must not keep the window open.");
    }

    [AvaloniaFact]
    public async Task FailedStopLeavesThePrimaryActionUsable()
    {
        var runtime = new UnstoppableRuntime();
        await using TestDesktop desktop = TestDesktop.Create(runtime: runtime);
        desktop.Window.Show();
        runtime.SetState(RuntimeState.Running);
        desktop.ViewModel.Tick(DateTimeOffset.UtcNow, 0);

        await desktop.ViewModel.ToggleRuntimeCommand.ExecuteAsync(null)
            .WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

        // The command must be re-armed, not left permanently disabled by a
        // failure that never completed.
        runtime.SetState(RuntimeState.Faulted);
        desktop.ViewModel.Tick(DateTimeOffset.UtcNow, 0);
        Assert.True(desktop.ViewModel.HasNotification);
        Assert.True(desktop.ViewModel.NotificationIsError);
        Assert.True(desktop.ViewModel.ToggleRuntimeCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ConfigurationWarningClearsItselfWhileErrorsRemain()
    {
        await using TestDesktop desktop = TestDesktop.Create(
            configText: DeprecatedPresetConfig);
        desktop.Window.Show();

        Assert.True(desktop.ViewModel.HasNotification);
        Assert.False(desktop.ViewModel.NotificationIsError);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        desktop.ViewModel.Tick(now, 0);
        Assert.True(desktop.ViewModel.HasNotification);

        desktop.ViewModel.Tick(
            now + MainWindowViewModel.TransientNotificationDuration +
                TimeSpan.FromSeconds(1),
            0);
        Assert.False(desktop.ViewModel.HasNotification);
    }

    private const string DeprecatedPresetConfig = """
        {
          "version": 1,
          "audio": { "device": "default" },
          "pipeline": {
            "vad": { "type": "webrtc", "preset": "balanced" },
            "speech": {
              "type": "openai-chat-audio",
              "baseUrl": "https://openrouter.ai/api/v1",
              "apiKey": "env:FOXTRANS_TEST_KEY",
              "model": "test/model",
              "prompt": "Translate this audio to English."
            }
          },
          "outputs": [ { "type": "vrchat-osc", "address": "127.0.0.1:9000" } ]
        }
        """;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(25);
    }

    private static T? Descendant<T>(Control root) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault();

    private static IReadOnlyList<T> Descendants<T>(Control root)
        where T : Control =>
        root.GetLogicalDescendants().OfType<T>().ToArray();

    private sealed class TestDesktop : IAsyncDisposable
    {
        private TestDesktop(
            string directory,
            DesktopApplicationServices services,
            MainWindowViewModel viewModel,
            MainWindow window)
        {
            Directory = directory;
            Services = services;
            ViewModel = viewModel;
            Window = window;
        }

        public string Directory { get; }
        public DesktopApplicationServices Services { get; }
        public MainWindowViewModel ViewModel { get; }
        public MainWindow Window { get; }

        public static TestDesktop Create(
            IFoxTransRuntime? runtime = null,
            string? configText = null,
            string secret = "test-key")
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "foxtrans-desktop-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            if (configText is not null)
            {
                File.WriteAllText(
                    Path.Combine(directory, "config.jsonc"),
                    configText);
            }
            var store = new DesktopPreferencesStore(
                Path.Combine(directory, "desktop-preferences.json"));
            DesktopApplicationServices services =
                DesktopApplicationServices.Create(
                    directory,
                    store,
                    runtime ?? new FoxTransRuntime(
                        new BlockingSessionFactory()),
                    new Devices(),
                    _ => secret);
            var viewModel = new MainWindowViewModel(services);
            var window = new MainWindow(viewModel);
            return new(directory, services, viewModel, window);
        }

        public async ValueTask DisposeAsync()
        {
            Window.Hide();
            await ViewModel.ShutdownAsync();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class Devices : IAudioInputDeviceCatalogue
    {
        public IReadOnlyList<AudioInputDevice> GetInputs() =>
            [new(0, "Studio microphone")];
    }

    private sealed class CountingDevices : IAudioInputDeviceCatalogue
    {
        public int EnumerationCount { get; private set; }

        public IReadOnlyList<AudioInputDevice> GetInputs()
        {
            EnumerationCount++;
            return [new(0, "Studio microphone")];
        }
    }

    /// <summary>
    /// A runtime whose stop always fails, standing in for a session that
    /// ignores cancellation. The shell must stay closable and usable anyway.
    /// </summary>
    private sealed class UnstoppableRuntime : IFoxTransRuntime
    {
        private int _state = (int)RuntimeState.Stopped;

        public bool StopAttempted { get; private set; }
        public bool IsDisposed { get; private set; }
        public RuntimeState State => (RuntimeState)Volatile.Read(ref _state);
        public ResolvedExecutionPlan? ActivePlan => null;
        public Task Completion => Task.CompletedTask;

        public void SetState(RuntimeState state) =>
            Volatile.Write(ref _state, (int)state);

        public Task StartAsync(
            ResolvedExecutionPlan plan,
            IAppReporter reporter,
            CancellationToken cancellationToken)
        {
            SetState(RuntimeState.Running);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopAttempted = true;
            SetState(RuntimeState.Faulted);
            return Task.FromException(new TimeoutException(
                "Runtime shutdown exceeded its bound."));
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingSessionFactory :
        IRuntimePipelineSessionFactory
    {
        public BlockingSession? Session { get; private set; }

        public ValueTask<IRuntimePipelineSession> CreateAsync(
            ResolvedExecutionPlan plan,
            CancellationToken cancellationToken)
        {
            Session = new();
            return ValueTask.FromResult<IRuntimePipelineSession>(Session);
        }
    }

    private sealed class BlockingSession : IRuntimePipelineSession
    {
        private int _disposed;
        public int DisposeCount => Volatile.Read(ref _disposed);

        public Task RunAsync(
            IAppReporter reporter,
            CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposed);
            return ValueTask.CompletedTask;
        }
    }
}
