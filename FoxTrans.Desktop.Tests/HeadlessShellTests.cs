using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
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
        VoiceOrbControl orb = Descendant<VoiceOrbControl>(window)!;
        orb.IsVisible = false;

        using var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.True(window.ClientSize.Width >= width - 1);
        Assert.True(window.ClientSize.Height >= height - 1);
        Assert.NotNull(Descendant<LiveStudioView>(window));
        Assert.NotNull(orb);
        Assert.Empty(Descendants<PipelinePageView>(window));
        Assert.Empty(Descendants<PipelineFlowView>(window));
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
        Assert.False(
            window.FindControl<Avalonia.Controls.Shapes.Path>(
                "MaximizeGlyph")!.IsVisible);
        Assert.True(
            window.FindControl<Avalonia.Controls.Shapes.Path>(
                "RestoreGlyph")!.IsVisible);
        window.WindowState = WindowState.Normal;
        Assert.True(
            window.FindControl<Avalonia.Controls.Shapes.Path>(
                "MaximizeGlyph")!.IsVisible);
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
        Assert.Empty(Descendants<PipelinePageView>(desktop.Window));
        Assert.True(desktop.ViewModel.TryCloseSettingsFromBackdrop());
        Assert.False(desktop.ViewModel.IsSettingsOpen);
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
    public void VoiceOrbRendersEveryTypedModeWithoutPolygonState(
        VoiceVisualizationMode mode)
    {
        var orb = new VoiceOrbControl
        {
            Width = 360,
            Height = 360,
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
            Width = 380,
            Height = 380,
            Content = orb
        };
        window.Show();

        using var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.Equal((float)mode, orb.CurrentState.StateValue);
        Assert.Equal(12, orb.CurrentState.SpectralBands.Length);
        Assert.Null(orb.ShaderCompilationError);
        window.Close();
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
        Descendant<VoiceOrbControl>(desktop.Window)!.IsVisible = false;

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
