using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using FoxTrans.Desktop.Models;
using FoxTrans.Desktop.Services;
using FoxTrans.Desktop.ViewModels;
using FoxTrans.Desktop.Views;

namespace FoxTrans.Desktop.Vr;

/// <summary>
/// Avalonia pixel producer for SteamVR. Its dispatcher timer is intentionally
/// independent from MainWindow scheduling: minimising the desktop UI does not
/// stop the headset surface or its SteamVR recovery probe.
/// </summary>
internal sealed class VrOverlayHost : IDisposable
{
    internal static readonly TimeSpan DisconnectedInterval =
        TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _timer;
    private readonly VrOverlaySupervisor _supervisor;
    private readonly Func<DesktopPreferences> _preferences;
    private Window? _styleHost;
    private VrOverlayView? _view;
    private VrOverlaySurface? _surface;
    private Func<DateTimeOffset, RuntimeSample> _sampleRuntime;
    private VrOverlayViewModel _viewModel;
    private long _lastDiagnosticEventCount = -1;
    private long _lastRenderedRevision = -1;
    private bool _started;
    private bool _disposed;

    public VrOverlayHost(
        PipelineViewDefinition definition,
        ResolvedExecutionPlan? plan,
        Func<DateTimeOffset, RuntimeSample> sampleRuntime,
        Func<DesktopPreferences> preferences,
        VrOverlaySupervisor? supervisor = null)
    {
        _sampleRuntime = sampleRuntime;
        _preferences = preferences;
        _supervisor = supervisor ?? new VrOverlaySupervisor();
        _supervisor.StatusChanged += OnStatusChanged;
        _viewModel = new VrOverlayViewModel(definition, plan);
        _timer = new DispatcherTimer { Interval = DisconnectedInterval };
        _timer.Tick += OnTick;
    }

    public event Action<VrOverlayStatus>? StatusChanged;

    public VrOverlayViewModel ViewModel => _viewModel;
    public VrOverlayStatus Status => _supervisor.Status;

    public void Start()
    {
        if (_disposed || _started)
            return;
        _started = true;
        _timer.Start();
        RenderTick();
    }

    public void UpdateRuntime(
        PipelineViewDefinition definition,
        ResolvedExecutionPlan? plan,
        Func<DateTimeOffset, RuntimeSample> sampleRuntime)
    {
        if (_disposed)
            return;
        _sampleRuntime = sampleRuntime;
        _viewModel = new VrOverlayViewModel(definition, plan)
        {
            PipelineName = _viewModel.PipelineName,
            ModelLine = _viewModel.ModelLine,
            ReducedMotion = _viewModel.ReducedMotion
        };
        if (_view is not null)
            _view.DataContext = _viewModel;
        _lastRenderedRevision = -1;
        _lastDiagnosticEventCount = -1;
    }

    public void UpdateHeader(string pipelineName, string modelLine)
    {
        if (_disposed)
            return;
        _viewModel.PipelineName = pipelineName;
        _viewModel.ModelLine = modelLine;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _supervisor.StatusChanged -= OnStatusChanged;
        _supervisor.Dispose();
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                DisposeVisuals();
                return;
            }
            Dispatcher.UIThread.Post(DisposeVisuals, DispatcherPriority.Send);
        }
        catch
        {
            // Process exit may already have released the dispatcher.
        }
    }

    private void DisposeVisuals()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        ReleaseVisuals();
    }

    internal static TimeSpan StatePollIntervalFor(
        int maximumStateChecksPerSecond)
    {
        int checksPerSecond =
            Math.Clamp(maximumStateChecksPerSecond, 1, 12);
        return TimeSpan.FromSeconds(1d / checksPerSecond);
    }

    private void OnTick(object? sender, EventArgs eventArgs) => RenderTick();

    internal void RenderTick()
    {
        if (_disposed)
            return;

        DesktopPreferences preferences = _preferences();
        VrOverlayPreferences overlay = preferences.VrOverlay;
        _viewModel.ApplyPreferences(overlay);
        _viewModel.ReducedMotion = true;
        _supervisor.SetEnabled(overlay.Enabled);
        _supervisor.Tick(ToPlacement(overlay), HasVisibleModule(overlay));

        if (!_supervisor.IsConnected)
        {
            _timer.Interval = DisconnectedInterval;
            ReleaseVisuals();
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        RuntimeSample sample = _sampleRuntime(now);
        _viewModel.Apply(
            sample.Snapshot,
            audioFrame: null,
            now,
            animationSeconds: 0,
            advanceVisuals: false);
        _viewModel.AdvanceText(now);
        WriteDiagnosticState(sample.Snapshot, now);

        // Poll semantic state promptly, but the revision gate below means this
        // does not imply a bitmap render or OpenVR upload on every tick.
        // The serialized preference keeps its historical property name, but
        // it now caps state sampling only. Rendering remains revision-driven.
        _timer.Interval =
            StatePollIntervalFor(overlay.MaxFramesPerSecond);
        if (_lastRenderedRevision == _viewModel.Revision)
            return;

        EnsureVisuals();
        ReadOnlyMemory<byte> rgba = _surface!.Render(_view!);
        _supervisor.Submit(
            rgba.Span,
            VrOverlaySurface.Width,
            VrOverlaySurface.Height);
        _lastRenderedRevision = _viewModel.Revision;
    }

    private void EnsureVisuals()
    {
        if (_surface is not null)
            return;

        _view = new VrOverlayView { DataContext = _viewModel };
        _styleHost = new Window
        {
            Width = VrOverlaySurface.LogicalWidth,
            Height = VrOverlaySurface.LogicalHeight,
            ShowInTaskbar = false,
            CanResize = false,
            WindowDecorations = WindowDecorations.None,
            Content = _view
        };
        _surface = new VrOverlaySurface();
        _lastRenderedRevision = -1;
    }

    private void ReleaseVisuals()
    {
        _surface?.Dispose();
        _surface = null;
        if (_styleHost is not null)
        {
            _styleHost.Content = null;
            _styleHost.Close();
            _styleHost = null;
        }
        _view = null;
        _lastRenderedRevision = -1;
    }

    private void WriteDiagnosticState(
        DesktopRuntimeSnapshot snapshot,
        DateTimeOffset now)
    {
        if (snapshot.EventCount == _lastDiagnosticEventCount)
            return;
        _lastDiagnosticEventCount = snapshot.EventCount;
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "FoxTrans");
            Directory.CreateDirectory(directory);
            string[] lines =
            [
                $"observedAt={now:O}",
                $"eventCount={snapshot.EventCount}",
                $"voiceMode={snapshot.VoiceMode}",
                $"snapshotTranslationChars={snapshot.Pipeline.Translation.Text.Length}",
                $"snapshotTranslationCurrent={snapshot.Pipeline.Translation.IsForCurrentSource}",
                $"projectedTranslationChars={_viewModel.TranslationText.Length}",
                $"displayTranslationChars={_viewModel.OverlayTranslationText.Length}",
                $"renderRevision={_viewModel.Revision}"
            ];
            File.WriteAllLines(
                Path.Combine(directory, "vr-overlay-state.txt"),
                lines);
        }
        catch
        {
            // Diagnostics are best-effort and must not affect the overlay.
        }
    }

    private void OnStatusChanged(VrOverlayStatus status) => StatusChanged?.Invoke(status);

    private static bool HasVisibleModule(VrOverlayPreferences value) =>
        value.ShowHeader || value.ShowVoiceStatus ||
        value.ShowRecognition || value.ShowTranslation;

    private static VrOverlayPlacement ToPlacement(VrOverlayPreferences value) => new(
        (float)value.WidthMeters,
        (float)value.DistanceMeters,
        (float)value.PitchDegrees,
        (float)value.YawDegrees,
        (float)value.VerticalOffsetMeters,
        (float)value.Opacity);
}
