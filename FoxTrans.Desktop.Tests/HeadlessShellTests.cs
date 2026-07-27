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
    [InlineData(960, 640)]
    [InlineData(800, 600)]
    public async Task ShellRendersAtRepresentativeSizes(
        double width,
        double height)
    {
        await using TestDesktop desktop = TestDesktop.Create();
        MainWindow window = desktop.Window;
        window.MinWidth = 0;
        window.MinHeight = 0;
        window.Show();
        window.Width = width;
        window.Height = height;

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(window.ClientSize.Width >= width - 1);
        Assert.True(window.ClientSize.Height >= height - 1);
        LiveStudioView? live = Descendant<LiveStudioView>(window);
        Assert.NotNull(live);
        Assert.NotNull(Descendant<VoiceOrbControl>(window));
        Assert.Equal(2, Descendants<StreamingTextPresenter>(window).Count);
        Border translationPanel =
            live!.FindControl<Border>("TranslationPanel")!;
        Assert.Equal(width == 800 ? 1 : 0, Grid.GetRow(translationPanel));
    }

    [AvaloniaFact]
    public async Task NavigationChangesPagesWithoutHidingLiveTextBehindTabs()
    {
        await using TestDesktop desktop = TestDesktop.Create();
        desktop.Window.Show();
        MainWindowViewModel viewModel = desktop.ViewModel;

        viewModel.NavigatePipelineCommand.Execute(null);
        Assert.True(viewModel.IsPipelineSelected);
        Assert.True(Descendant<PipelinePageView>(desktop.Window)!.IsVisible);

        viewModel.NavigateSettingsCommand.Execute(null);
        Assert.True(viewModel.IsSettingsSelected);
        Assert.True(Descendant<SettingsView>(desktop.Window)!.IsVisible);

        viewModel.NavigateLiveCommand.Execute(null);
        Assert.True(viewModel.IsLiveSelected);
        Assert.Equal(2, Descendants<StreamingTextPresenter>(desktop.Window).Count);
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

        await desktop.ViewModel.ToggleRuntimeCommand.ExecuteAsync(null);
        desktop.ViewModel.Tick(DateTimeOffset.UtcNow, 0.2);
        Assert.Equal(RuntimeState.Stopped, desktop.ViewModel.RuntimeState);
        Assert.Equal("Start", desktop.ViewModel.PrimaryActionText);
        Assert.Equal(1, factory.Session!.DisposeCount);
    }

    [AvaloniaFact]
    public async Task ShowingTheDesktopNeverStartsMicrophoneRuntimeAutomatically()
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
    public void VoiceOrbControlRendersEveryTypedMode(
        VoiceVisualizationMode mode)
    {
        var orb = new VoiceOrbControl
        {
            Width = 320,
            Height = 320,
            Mode = mode,
            AnimationSeconds = 1.5,
            AudioFrame = new(
                0.25f,
                0.5f,
                false,
                true,
                Enumerable.Repeat(0.35f, Pcm16AudioFeatureExtractor.SpectrumBandCount).ToArray(),
                1,
                DateTimeOffset.UnixEpoch)
        };
        var window = new Window
        {
            Width = 340,
            Height = 340,
            Content = orb
        };
        window.Show();

        using var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.InRange(orb.CurrentState.CoreScale, 0.75f, 1.5f);
        Assert.Equal(VoiceOrbAnimationModel.ContourPointCount, orb.CurrentState.Contour.Length);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ActualPipelineNodesAndLongWrappingTextRemainVisible()
    {
        await using TestDesktop desktop = TestDesktop.Create();
        desktop.Window.Show();

        string[] stageTitles = desktop.ViewModel.Live.Stages
            .Select(stage => stage.Title)
            .ToArray();
        Assert.Equal(
            ["Microphone", "WebRTC VAD", "Audio LLM", "VRChat OSC"],
            stageTitles);
        Assert.All(
            Descendants<StreamingTextPresenter>(desktop.Window),
            presenter => Assert.All(
                Descendants<TextBlock>(presenter),
                block => Assert.Equal(TextWrapping.Wrap, block.TextWrapping)));
    }

    [AvaloniaFact]
    public async Task ConfigurationErrorViewRendersAndWindowRemainsUsable()
    {
        await using TestDesktop desktop = TestDesktop.Create(
            configText: "{ invalid json");
        desktop.Window.Show();

        Assert.False(desktop.ViewModel.HasValidPlan);
        Assert.True(desktop.ViewModel.HasNotification);
        desktop.ViewModel.NavigatePipelineCommand.Execute(null);
        Assert.True(desktop.ViewModel.Pipeline.HasProblems);
        Assert.NotEmpty(desktop.ViewModel.Pipeline.Problems);
        Assert.True(Descendant<PipelinePageView>(desktop.Window)!.IsVisible);
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
    public async Task RenderedVisualTreeContainsNoResolvedSecret()
    {
        const string secret = "headless-environment-secret";
        await using TestDesktop desktop = TestDesktop.Create(secret: secret);
        desktop.Window.Show();

        string visibleText = string.Join(
            "\n",
            Descendants<TextBlock>(desktop.Window)
                .Select(block => block.Text ?? ""));

        Assert.DoesNotContain(secret, visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", visibleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("full direct prompt", visibleText, StringComparison.Ordinal);
    }

    private static T? Descendant<T>(Control root) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault();

    private static IReadOnlyList<T> Descendants<T>(Control root) where T : Control =>
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
                    runtime ?? new FoxTransRuntime(new BlockingSessionFactory()),
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

    private sealed class BlockingSessionFactory : IRuntimePipelineSessionFactory
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
