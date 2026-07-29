using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FoxTrans.Desktop.Models;
using FoxTrans.Desktop.Services;

namespace FoxTrans.Desktop.ViewModels;

internal enum VisualTickMode
{
    None,
    Processing,
    Active
}

public sealed class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// How long an informational or warning notice stays on screen. Errors have
    /// no deadline: they describe state the user still has to act on.
    /// </summary>
    public static readonly TimeSpan TransientNotificationDuration =
        TimeSpan.FromSeconds(8);

    private readonly DesktopApplicationServices _services;
    private DesktopEventBridge _bridge;
    private LiveStudioViewModel _live;
    private RuntimeState _runtimeState;
    private string _pipelineName = "Loading…";
    private string _modelLine = "";
    private string _notificationTitle = "";
    private string _notificationDetail = "";
    private bool _hasNotification;
    private bool _notificationIsError;
    private bool _isSettingsOpen;
    private bool _isInitializing;
    private DateTimeOffset _lastNotificationAt;
    private DateTimeOffset? _notificationExpiresAt;
    private int _initialized;
    private int _shutdown;
#if UI_CAPTURE
    private DesignPreviewState? _designPreview;
#endif

    public MainWindowViewModel(
        DesktopApplicationServices services
#if UI_CAPTURE
        ,
        string? requestedDesignPreview = null,
        string? requestedCapturePath = null
#endif
        )
    {
        _services = services;
#if UI_CAPTURE
        RequestedDesignPreview = requestedDesignPreview;
        RequestedCapturePath = requestedCapturePath;
#endif
        DesktopBootstrapResult bootstrap = services.Bootstrap;
        _isInitializing = bootstrap.IsPending;
        PipelineViewDefinition definition = Definition(bootstrap);
        _bridge = new(definition, bootstrap.Plan);
        _live = new(definition, bootstrap.Plan, _isInitializing);
        _runtimeState = RuntimeState.Stopped;

        Settings = new(
            services,
            SettingsChanged,
            ConfigurationSavedAsync);
        Settings.CloseRequested += () => IsSettingsOpen = false;

        ToggleRuntimeCommand = new AsyncRelayCommand(
            ToggleRuntimeAsync,
            CanToggleRuntime);
        OpenSettingsCommand = new RelayCommand(
            () => IsSettingsOpen = true);
        CloseSettingsCommand = new RelayCommand(
            Settings.RequestClose);
        DismissNotificationCommand = new RelayCommand(DismissNotification);

        ApplyHeader(bootstrap);
        if (!bootstrap.IsPending)
            PresentBootstrapState(bootstrap);
    }

    public event Action<DesktopAppearance>? AppearanceChanged;
#if UI_CAPTURE
    public string? RequestedDesignPreview { get; }
    public string? RequestedCapturePath { get; }
#endif

    public LiveStudioViewModel Live
    {
        get => _live;
        private set => SetProperty(ref _live, value);
    }

    public SettingsViewModel Settings { get; }

    public string PipelineName
    {
        get => _pipelineName;
        private set => SetProperty(ref _pipelineName, value);
    }

    public string ModelLine
    {
        get => _modelLine;
        private set => SetProperty(ref _modelLine, value);
    }

    public bool HasValidPlan => _services.Bootstrap.Plan is not null;

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetProperty(ref _isSettingsOpen, value);
    }

    public bool IsInitializing
    {
        get => _isInitializing;
        private set
        {
            if (!SetProperty(ref _isInitializing, value))
                return;
            OnPropertyChanged(nameof(PrimaryActionText));
            OnPropertyChanged(nameof(IsRuntimeBusy));
            ToggleRuntimeCommand.NotifyCanExecuteChanged();
        }
    }

    public IAsyncRelayCommand ToggleRuntimeCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IRelayCommand CloseSettingsCommand { get; }
    public IRelayCommand DismissNotificationCommand { get; }

    public RuntimeState RuntimeState
    {
        get => _runtimeState;
        private set
        {
            if (!SetProperty(ref _runtimeState, value))
                return;
            Settings.UpdateRuntimeState(value);
            OnPropertyChanged(nameof(PrimaryActionText));
            OnPropertyChanged(nameof(IsRuntimeBusy));
            ToggleRuntimeCommand.NotifyCanExecuteChanged();
        }
    }

    public string PrimaryActionText
    {
        get
        {
            if (IsInitializing)
                return "Loading…";
            return RuntimeState switch
            {
                RuntimeState.Running => "Stop",
                RuntimeState.Starting => "Starting…",
                RuntimeState.Stopping => "Stopping…",
                RuntimeState.Faulted => "Start again",
                _ => "Start"
            };
        }
    }

    public bool IsRuntimeBusy =>
        IsInitializing ||
        RuntimeState is RuntimeState.Starting or RuntimeState.Stopping;

    internal VisualTickMode GetVisualTickMode(DateTimeOffset now) =>
#if UI_CAPTURE
        _designPreview is not null
            ? VisualTickMode.Active
            :
#endif
        Live.GetVisualTickMode(now);

    public string NotificationTitle
    {
        get => _notificationTitle;
        private set => SetProperty(ref _notificationTitle, value);
    }

    public string NotificationDetail
    {
        get => _notificationDetail;
        private set => SetProperty(ref _notificationDetail, value);
    }

    public bool HasNotification
    {
        get => _hasNotification;
        private set => SetProperty(ref _hasNotification, value);
    }

    public bool NotificationIsError
    {
        get => _notificationIsError;
        private set => SetProperty(ref _notificationIsError, value);
    }

    public DesktopPreferences Preferences => _services.Preferences;

    public async Task InitializeAsync()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;
        if (!_services.Bootstrap.IsPending)
            return;

        IsInitializing = true;
        try
        {
            DesktopBootstrapResult bootstrap =
                await Task.Run(() => _services.Reload());
            await ReplaceBootstrapAsync(bootstrap);
            PresentBootstrapState(bootstrap);
        }
        catch (Exception exception)
        {
            IsSettingsOpen = true;
            Settings.SetFeedback(
                DiagnosticText.Safe(exception.Message, 800),
                true);
            ShowNotification(
                "Configuration needs attention",
                DiagnosticText.Safe(exception.Message, 800),
                true,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            IsInitializing = false;
        }
    }

#if UI_CAPTURE
    public void ApplyDesignPreview(string? scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario))
            return;

        string normalized = scenario.Trim().ToLowerInvariant();
        double animationOffset = 0;
        if (normalized.StartsWith(
                "waveform-frame-",
                StringComparison.Ordinal) &&
            int.TryParse(
                normalized["waveform-frame-".Length..],
                out int frameIndex))
        {
            animationOffset = Math.Clamp(frameIndex, 0, 11) * 0.43;
            normalized = "listening-loud";
        }
        VoiceVisualizationMode mode = normalized switch
        {
            "listening-quiet" => VoiceVisualizationMode.Listening,
            "listening-loud" => VoiceVisualizationMode.Speech,
            "processing" => VoiceVisualizationMode.Processing,
            "success" => VoiceVisualizationMode.Success,
            "error" => VoiceVisualizationMode.Error,
            "compact" => VoiceVisualizationMode.Speech,
            "audio-llm" => VoiceVisualizationMode.Listening,
            _ => VoiceVisualizationMode.Idle
        };
        float level = normalized switch
        {
            "listening-quiet" => 0.045f,
            "listening-loud" => 0.34f,
            "compact" => 0.22f,
            _ => 0.09f
        };
        bool speech = mode == VoiceVisualizationMode.Speech;
        float[] bands =
        [
            level * 1.15f,
            level,
            level * 0.92f,
            level * 0.82f,
            level * 0.74f,
            level * 0.68f,
            level * 0.61f,
            level * 0.54f,
            level * 0.48f,
            level * 0.42f,
            level * 0.35f,
            level * 0.30f
        ];
        var frame = new AudioVisualFrame(
            level,
            Math.Min(0.96f, level * 2.35f),
            false,
            speech,
            bands,
            1,
            DateTimeOffset.UtcNow);
        bool showRecognition =
            normalized != "audio-llm" &&
            Live.HasRecognition;
        _designPreview = new(
            mode,
            frame,
            showRecognition
                ? "Я сейчас проверяю, насколько хорошо это работает…"
                : "",
            normalized == "processing"
                ? "I am preparing the latest translation…"
                : "I am testing how well this is working…",
            showRecognition,
            animationOffset);

        if (normalized == "settings-pipeline")
        {
            Settings.SelectedSection = SettingsSection.Pipeline;
            IsSettingsOpen = true;
        }
        else if (normalized == "settings-whisper-providers")
        {
            Settings.Mode = DesktopPipelineMode.WhisperLlm;
            Settings.SelectedSection = SettingsSection.Providers;
            IsSettingsOpen = true;
        }
        else if (normalized == "settings-validation")
        {
            Settings.Mode = DesktopPipelineMode.WhisperLlm;
            Settings.SelectedSection = SettingsSection.Providers;
            Settings.Transcription.Endpoint = "not-a-valid-endpoint";
            Settings.Transcription.SetEndpointError(
                "Endpoint must be an absolute HTTP or HTTPS URL.");
            Settings.SetFeedback(
                "Correct the highlighted values before saving.",
                true);
            IsSettingsOpen = true;
        }
    }
#endif

    public void Tick(
        DateTimeOffset now,
        double animationSeconds,
        bool advanceVisuals = true)
    {
#if UI_CAPTURE
        if (_designPreview is { } preview)
        {
            Live.ReducedMotion = Settings.ReducedMotion;
            Live.ApplyPreview(
                preview.Mode,
                preview.Frame with { ObservedAt = now },
                preview.Source,
                preview.Translation,
                preview.ShowRecognition,
                now,
                animationSeconds + preview.AnimationOffset);
            return;
        }
#endif

        if (IsInitializing)
            return;

        RuntimeState = _services.Runtime.State;
        DesktopRuntimeSnapshot snapshot =
            DesktopRuntimeReducer.Advance(_bridge.Snapshot, now);
        AudioVisualFrame? audioFrame = FreshAudioFrame(
            _bridge.LatestAudioFrame,
            now);
        if (Settings.IsMicrophoneTesting)
        {
            audioFrame = Settings.MicrophoneTestFrame;
            snapshot = snapshot with
            {
                RuntimeState = RuntimeState.Running,
                VoiceMode = audioFrame?.IsSpeechActive == true ||
                    audioFrame?.Rms > 0.055f
                    ? VoiceVisualizationMode.Speech
                    : VoiceVisualizationMode.Listening
            };
        }

        Live.ReducedMotion = Settings.ReducedMotion;
        Live.Apply(
            snapshot,
            audioFrame,
            now,
            animationSeconds,
            advanceVisuals);
        if (advanceVisuals)
            Live.AdvanceText(now);

        if (snapshot.Notification is { } notification &&
            notification.ObservedAt > _lastNotificationAt)
        {
            ShowNotification(
                notification.Title,
                notification.Detail,
                notification.Severity == DesktopNotificationSeverity.Error,
                notification.ObservedAt,
                now);
        }
        else
        {
            ExpireNotification(now);
        }
    }

    public bool TryCloseSettingsFromBackdrop()
    {
        if (!Settings.HasUnsavedChanges)
        {
            IsSettingsOpen = false;
            return true;
        }
        Settings.RequestClose();
        return false;
    }

    public void UpdateWindowPreferences(
        double width,
        double height,
        int? x,
        int? y)
    {
        _services.Preferences = Settings.ApplyTo(
            _services.Preferences with
            {
                WindowWidth = width,
                WindowHeight = height,
                WindowX = x,
                WindowY = y
            });
        TrySavePreferences();
    }

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
            return;
        try
        {
            if (_services.Runtime.State is not RuntimeState.Stopped)
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(6));
                await _services.Runtime.StopAsync(timeout.Token);
            }
        }
        catch
        {
            // A pipeline that refuses to stop is already reported as a fault.
            // Shutdown continues so the window is never held open by it.
        }

        TrySavePreferences();
        // Each owner is released independently: one failing disposal must not
        // leave a microphone, socket, or pump running behind it.
        await DisposeQuietlyAsync(Settings);
        await DisposeQuietlyAsync(_bridge);
        await DisposeQuietlyAsync(_services);
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync();
        }
        catch
        {
            // Disposal diagnostics have no surface left to report to.
        }
    }

    private bool CanToggleRuntime() =>
        !IsInitializing &&
        HasValidPlan &&
        RuntimeState is
            RuntimeState.Stopped or
            RuntimeState.Running or
            RuntimeState.Faulted;

    private async Task ToggleRuntimeAsync()
    {
        try
        {
            RuntimeState state = _services.Runtime.State;
            if (state == RuntimeState.Running)
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(6));
                await _services.Runtime.StopAsync(timeout.Token);
            }
            else if (state is RuntimeState.Stopped or RuntimeState.Faulted &&
                     _services.Bootstrap.Plan is { } plan)
            {
                // The previous run's last error describes a run that is over.
                DismissNotification();
                await _services.Runtime.StartAsync(
                    plan,
                    _bridge,
                    CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            ShowNotification(
                "Could not change runtime state",
                DiagnosticText.Safe(exception.Message, 800),
                true,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            RuntimeState = _services.Runtime.State;
        }
    }

    private async Task ConfigurationSavedAsync(
        DesktopConfigurationSaveResult result,
        bool restart)
    {
        if (result.Bootstrap is not { } bootstrap)
            return;

        if (restart &&
            _services.Runtime.State is not RuntimeState.Stopped)
        {
            try
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(6));
                await _services.Runtime.StopAsync(timeout.Token);
            }
            catch (Exception exception)
            {
                // The saved file is still valid; only the automatic restart is
                // lost. The user keeps a usable window and an explicit Start.
                restart = false;
                ShowNotification(
                    "Could not stop the running pipeline",
                    DiagnosticText.Safe(exception.Message, 800),
                    true,
                    DateTimeOffset.UtcNow);
            }
        }

        _services.AcceptBootstrap(bootstrap);
        await ReplaceBootstrapAsync(bootstrap);
        if (restart && bootstrap.Plan is { } plan)
        {
            Settings.SetFeedback("Configuration saved · Restarting");
            try
            {
                await _services.Runtime.StartAsync(
                    plan,
                    _bridge,
                    CancellationToken.None);
                Settings.SetFeedback("Configuration saved · Running");
            }
            catch (Exception exception)
            {
                ShowNotification(
                    "Could not restart the pipeline",
                    DiagnosticText.Safe(exception.Message, 800),
                    true,
                    DateTimeOffset.UtcNow);
                Settings.SetFeedback(
                    "Configuration saved · Start it again when ready",
                    true);
            }
            finally
            {
                RuntimeState = _services.Runtime.State;
            }
        }
        else
        {
            Settings.SetFeedback("Configuration saved");
            RuntimeState = _services.Runtime.State;
        }
    }

    private async Task ReplaceBootstrapAsync(
        DesktopBootstrapResult bootstrap)
    {
        PipelineViewDefinition definition = Definition(bootstrap);
        var nextBridge = new DesktopEventBridge(definition, bootstrap.Plan);
        var nextLive = new LiveStudioViewModel(definition, bootstrap.Plan)
        {
            ReducedMotion = Settings.ReducedMotion
        };
        DesktopEventBridge previous = _bridge;
        _bridge = nextBridge;
        Live = nextLive;
        Settings.ReplaceConfiguration(
            bootstrap.Config ?? AppConfig.Default(),
            bootstrap.AudioInputs);
        ApplyHeader(bootstrap);
        OnPropertyChanged(nameof(HasValidPlan));
        ToggleRuntimeCommand.NotifyCanExecuteChanged();
        await previous.DisposeAsync();
    }

    private void PresentBootstrapState(DesktopBootstrapResult bootstrap)
    {
        var redactor = new DesktopSecretRedactor(
            bootstrap.Plan,
            bootstrap.Config);
        if (!bootstrap.IsValid)
        {
            string detail = bootstrap.Issues.FirstOrDefault() is { } issue
                ? redactor.Redact(issue.Message)
                : "Complete the required settings to continue.";
            IsSettingsOpen = true;
            Settings.SetFeedback(detail, true);
            ShowNotification(
                "Configuration needs attention",
                detail,
                true,
                DateTimeOffset.UtcNow);
        }
        else if (bootstrap.Warnings.Count > 0)
        {
            ShowNotification(
                "Configuration warning",
                redactor.Redact(bootstrap.Warnings[0]),
                false,
                DateTimeOffset.UtcNow);
        }
    }

    private void ApplyHeader(DesktopBootstrapResult bootstrap)
    {
        if (bootstrap.IsPending)
        {
            PipelineName = "Loading…";
            ModelLine = "";
            return;
        }

        FoxTransConfig? config = bootstrap.Config;
        PipelineConfig? pipeline = config?.EffectivePipeline;
        switch (pipeline?.Speech)
        {
            case OpenAiChatAudioConfig direct:
                PipelineName = "Audio LLM";
                ModelLine = Clean(direct.Model);
                break;
            case OpenAiTranscriptionConfig transcription:
                PipelineName = "Whisper + LLM";
                ModelLine = JoinModels(
                    transcription.Model,
                    (pipeline.Translation as OpenAiChatConfig)?.Model);
                break;
            case VoxtralFoxConfig:
                PipelineName = "Voxtral + LLM";
                ModelLine = JoinModels(
                    "Voxtral realtime",
                    (pipeline.Translation as OpenAiChatConfig)?.Model);
                break;
            default:
                PipelineName = bootstrap.Plan is null
                    ? "Configuration required"
                    : bootstrap.Plan.PipelineKind.ToString();
                ModelLine = "";
                break;
        }
    }

    private void SettingsChanged()
    {
        _services.Preferences = Settings.ApplyTo(_services.Preferences);
        Live.ReducedMotion = Settings.ReducedMotion;
        AppearanceChanged?.Invoke(Settings.Appearance);
        TrySavePreferences();
    }

    private void ShowNotification(
        string title,
        string detail,
        bool isError,
        DateTimeOffset observedAt,
        DateTimeOffset? shownAt = null)
    {
        NotificationTitle = title;
        NotificationDetail = DiagnosticText.Safe(detail, 800);
        NotificationIsError = isError;
        HasNotification = true;
        _lastNotificationAt = observedAt;
        _notificationExpiresAt = isError
            ? null
            : (shownAt ?? DateTimeOffset.UtcNow) + TransientNotificationDuration;
    }

    private void DismissNotification()
    {
        HasNotification = false;
        _notificationExpiresAt = null;
    }

    private void ExpireNotification(DateTimeOffset now)
    {
        if (!HasNotification ||
            _notificationExpiresAt is not { } deadline ||
            now < deadline)
        {
            return;
        }
        DismissNotification();
    }

    private void TrySavePreferences()
    {
        try
        {
            _services.SavePreferences();
        }
        catch
        {
            // Preferences are non-critical and never interrupt the pipeline.
        }
    }

    private static PipelineViewDefinition Definition(
        DesktopBootstrapResult bootstrap) =>
        bootstrap.Plan is null
            ? DesktopBootstrap.PlaceholderTopology()
            : PipelineTopologyBuilder.Build(bootstrap.Plan);

    private static string JoinModels(string? first, string? second) =>
        string.Join(
            "  ",
            new[] { Clean(first), Clean(second) }
                .Where(value => value.Length > 0));

    private static string Clean(string? value) =>
        value?.Trim() ?? "";

    private static AudioVisualFrame? FreshAudioFrame(
        AudioVisualFrame? frame,
        DateTimeOffset now) =>
        frame is not null &&
        (frame.ObservedAt > now ||
         now - frame.ObservedAt <= TimeSpan.FromMilliseconds(120))
            ? frame
            : null;

#if UI_CAPTURE
    private sealed record DesignPreviewState(
        VoiceVisualizationMode Mode,
        AudioVisualFrame Frame,
        string Source,
        string Translation,
        bool ShowRecognition,
        double AnimationOffset);
#endif
}
