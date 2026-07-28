using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FoxTrans.Desktop.Services;

namespace FoxTrans.Desktop.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly AudioFormat DesktopAudioFormat =
        new(16000, 16, 1);

    private readonly DesktopApplicationServices _services;
    private readonly Action _preferencesChanged;
    private readonly Func<
        DesktopConfigurationSaveResult,
        bool,
        Task> _configurationSaved;
    private readonly MicrophoneTestSession _microphoneTest = new();
    private FoxTransConfig _originalConfig;
    private SettingsSection _selectedSection;
    private DesktopPipelineMode _mode;
    private AudioDeviceOption? _selectedAudioDevice;
    private string _vadPreset = "natural-speech";
    private string _vadStartAfterMs = "";
    private string _vadStopAfterMs = "";
    private string _vadPreRollMs = "";
    private string _vadMinimumPhraseMs = "";
    private string _vadTimingError = "";
    private string _realtimePreset = "balanced";
    private string _realtimeMinimumIntervalMs = "";
    private string _realtimeMaximumIntervalMs = "";
    private string _realtimeMinimumChangedWords = "";
    private string _realtimeNewUtteranceAfterMs = "";
    private string _realtimeMaxSourceCharacters = "";
    private string _realtimeTimingError = "";
    private DesktopAppearance _appearance;
    private bool _reducedMotion;
    private bool _showAdvancedVad;
    private bool _showAdvancedRealtime;
    private bool _isDirty;
    private bool _isRuntimeActive;
    private bool _isSaving;
    private bool _isRefreshingDevices;
    private bool _isMicrophoneTesting;
    private string _feedback = "";
    private bool _feedbackIsError;
    private bool _suppressDirty;
    private int _disposed;

    public SettingsViewModel(
        DesktopApplicationServices services,
        Action preferencesChanged,
        Func<DesktopConfigurationSaveResult, bool, Task> configurationSaved)
    {
        _services = services;
        _preferencesChanged = preferencesChanged;
        _configurationSaved = configurationSaved;
        _originalConfig = services.Bootstrap.Config ?? AppConfig.Default();
        _appearance = services.Preferences.Appearance;
        _reducedMotion = services.Preferences.ReducedMotion;

        SelectPipelineCommand = new RelayCommand<DesktopPipelineMode>(
            mode => Mode = mode);
        SelectSectionCommand = new RelayCommand<SettingsSection>(
            section => SelectedSection = section);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        DiscardCommand = new RelayCommand(Discard);
        RefreshDevicesCommand = new AsyncRelayCommand(
            RefreshDevicesAsync,
            () => !IsRefreshingDevices);
        ToggleMicrophoneTestCommand = new AsyncRelayCommand(
            ToggleMicrophoneTestAsync,
            CanToggleMicrophoneTest);
        AddOutputCommand = new RelayCommand(AddOutput);
        ToggleAdvancedVadCommand = new RelayCommand(
            () => ShowAdvancedVad = !ShowAdvancedVad);
        ToggleAdvancedRealtimeCommand = new RelayCommand(
            () => ShowAdvancedRealtime = !ShowAdvancedRealtime);
        OpenConfigFolderCommand = new RelayCommand(OpenConfigFolder);
        OpenRawConfigCommand = new RelayCommand(OpenRawConfig);
        CopyDiagnosticsCommand = new RelayCommand(
            () => CopyDiagnosticsRequested?.Invoke(SanitizedDiagnostics));
        RequestCloseCommand = new RelayCommand(RequestClose);

        LoadFrom(_originalConfig);
    }

    public event Action? CloseRequested;
    public event Action<string>? CopyDiagnosticsRequested;

    public IReadOnlyList<DesktopAppearance> Appearances { get; } =
        Enum.GetValues<DesktopAppearance>();
    public IReadOnlyList<string> VadPresets { get; } =
        global::VadPresets.All.Select(preset => preset.Name).ToArray();
    public IReadOnlyList<string> RealtimePresets { get; } =
        global::RealtimePresets.All.Select(preset => preset.Name).ToArray();

    public ObservableCollection<AudioDeviceOption> AudioDevices { get; } = [];
    public ObservableCollection<OutputEditorViewModel> Outputs { get; } = [];
    public ObservableCollection<ConfigIssueViewModel> ValidationIssues { get; } = [];

    public AudioLlmProviderEditorViewModel AudioLlm { get; private set; } = null!;
    public TranscriptionProviderEditorViewModel Transcription { get; private set; } = null!;
    public VoxtralProviderEditorViewModel Voxtral { get; private set; } = null!;
    public TranslatorProviderEditorViewModel Translator { get; private set; } = null!;

    public IRelayCommand<DesktopPipelineMode> SelectPipelineCommand { get; }
    public IRelayCommand<SettingsSection> SelectSectionCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand DiscardCommand { get; }
    public IAsyncRelayCommand RefreshDevicesCommand { get; }
    public IAsyncRelayCommand ToggleMicrophoneTestCommand { get; }
    public IRelayCommand AddOutputCommand { get; }
    public IRelayCommand ToggleAdvancedVadCommand { get; }
    public IRelayCommand ToggleAdvancedRealtimeCommand { get; }
    public IRelayCommand OpenConfigFolderCommand { get; }
    public IRelayCommand OpenRawConfigCommand { get; }
    public IRelayCommand CopyDiagnosticsCommand { get; }
    public IRelayCommand RequestCloseCommand { get; }

    public SettingsSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (!SetProperty(ref _selectedSection, value))
                return;
            RaiseSectionVisibility();
        }
    }

    public bool IsPipelineSection =>
        SelectedSection == SettingsSection.Pipeline;
    public bool IsAudioSection =>
        SelectedSection == SettingsSection.Audio;
    public bool IsProvidersSection =>
        SelectedSection == SettingsSection.Providers;
    public bool IsOutputSection =>
        SelectedSection == SettingsSection.Output;
    public bool IsAppearanceSection =>
        SelectedSection == SettingsSection.Appearance;
    public bool IsAdvancedSection =>
        SelectedSection == SettingsSection.Advanced;

    public DesktopPipelineMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value))
                return;
            OnPropertyChanged(nameof(IsAudioLlm));
            OnPropertyChanged(nameof(IsWhisperLlm));
            OnPropertyChanged(nameof(IsVoxtralLlm));
            OnPropertyChanged(nameof(UsesVad));
            OnPropertyChanged(nameof(UsesTranslator));
            OnPropertyChanged(nameof(PipelineDisplayName));
            OnPropertyChanged(nameof(PipelineDescription));
            OnPropertyChanged(nameof(PipelineFlowSummary));
            MarkDirty();
        }
    }

    public bool IsAudioLlm => Mode == DesktopPipelineMode.AudioLlm;
    public bool IsWhisperLlm => Mode == DesktopPipelineMode.WhisperLlm;
    public bool IsVoxtralLlm => Mode == DesktopPipelineMode.VoxtralLlm;
    public bool UsesVad => !IsVoxtralLlm;
    public bool UsesTranslator => !IsAudioLlm;

    public string PipelineDisplayName => Mode switch
    {
        DesktopPipelineMode.AudioLlm => "Audio LLM",
        DesktopPipelineMode.WhisperLlm => "Whisper + LLM",
        _ => "Voxtral + LLM"
    };

    public string PipelineDescription => Mode switch
    {
        DesktopPipelineMode.AudioLlm =>
            "Audio is translated directly by one multimodal model.",
        DesktopPipelineMode.WhisperLlm =>
            "Speech is transcribed first, then translated by a text model.",
        _ =>
            "Continuous realtime transcription with incremental translation."
    };

    public string PipelineFlowSummary => Mode switch
    {
        DesktopPipelineMode.AudioLlm =>
            "Microphone  →  VAD  →  Audio LLM  →  VRChat",
        DesktopPipelineMode.WhisperLlm =>
            "Microphone  →  VAD  →  Whisper  →  Translator  →  VRChat",
        _ =>
            "Microphone  →  Voxtral  →  Translator  →  VRChat"
    };

    public AudioDeviceOption? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (SetProperty(ref _selectedAudioDevice, value))
                MarkDirty();
        }
    }

    public string ResolvedSampleFormat =>
        "Mono · 16 kHz · PCM16LE";

    public string VadPreset
    {
        get => _vadPreset;
        set
        {
            if (SetProperty(ref _vadPreset, value))
                MarkDirty();
        }
    }

    public string VadStartAfterMs
    {
        get => _vadStartAfterMs;
        set => SetTiming(ref _vadStartAfterMs, value);
    }

    public string VadStopAfterMs
    {
        get => _vadStopAfterMs;
        set => SetTiming(ref _vadStopAfterMs, value);
    }

    public string VadPreRollMs
    {
        get => _vadPreRollMs;
        set => SetTiming(ref _vadPreRollMs, value);
    }

    public string VadMinimumPhraseMs
    {
        get => _vadMinimumPhraseMs;
        set => SetTiming(ref _vadMinimumPhraseMs, value);
    }

    public string VadTimingError
    {
        get => _vadTimingError;
        private set
        {
            if (SetProperty(ref _vadTimingError, value))
                OnPropertyChanged(nameof(HasVadTimingError));
        }
    }

    public bool HasVadTimingError => VadTimingError.Length > 0;

    public string RealtimePreset
    {
        get => _realtimePreset;
        set
        {
            if (SetProperty(ref _realtimePreset, value))
                MarkDirty();
        }
    }

    public string RealtimeMinimumIntervalMs
    {
        get => _realtimeMinimumIntervalMs;
        set => SetTiming(ref _realtimeMinimumIntervalMs, value);
    }

    public string RealtimeMaximumIntervalMs
    {
        get => _realtimeMaximumIntervalMs;
        set => SetTiming(ref _realtimeMaximumIntervalMs, value);
    }

    public string RealtimeMinimumChangedWords
    {
        get => _realtimeMinimumChangedWords;
        set => SetTiming(ref _realtimeMinimumChangedWords, value);
    }

    public string RealtimeNewUtteranceAfterMs
    {
        get => _realtimeNewUtteranceAfterMs;
        set => SetTiming(ref _realtimeNewUtteranceAfterMs, value);
    }

    public string RealtimeMaxSourceCharacters
    {
        get => _realtimeMaxSourceCharacters;
        set => SetTiming(ref _realtimeMaxSourceCharacters, value);
    }

    public string RealtimeTimingError
    {
        get => _realtimeTimingError;
        private set
        {
            if (SetProperty(ref _realtimeTimingError, value))
                OnPropertyChanged(nameof(HasRealtimeTimingError));
        }
    }

    public bool HasRealtimeTimingError => RealtimeTimingError.Length > 0;

    public DesktopAppearance Appearance
    {
        get => _appearance;
        set
        {
            if (!SetProperty(ref _appearance, value))
                return;
            OnPropertyChanged(nameof(IsSystemAppearance));
            OnPropertyChanged(nameof(IsDarkAppearance));
            OnPropertyChanged(nameof(IsLightAppearance));
            _preferencesChanged();
        }
    }

    public bool IsSystemAppearance
    {
        get => Appearance == DesktopAppearance.System;
        set
        {
            if (value)
                Appearance = DesktopAppearance.System;
        }
    }

    public bool IsDarkAppearance
    {
        get => Appearance == DesktopAppearance.Dark;
        set
        {
            if (value)
                Appearance = DesktopAppearance.Dark;
        }
    }

    public bool IsLightAppearance
    {
        get => Appearance == DesktopAppearance.Light;
        set
        {
            if (value)
                Appearance = DesktopAppearance.Light;
        }
    }

    public bool ReducedMotion
    {
        get => _reducedMotion;
        set
        {
            if (!SetProperty(ref _reducedMotion, value))
                return;
            _preferencesChanged();
        }
    }

    public bool ShowAdvancedVad
    {
        get => _showAdvancedVad;
        set
        {
            if (!SetProperty(ref _showAdvancedVad, value))
                return;
            OnPropertyChanged(nameof(AdvancedVadActionText));
        }
    }

    public string AdvancedVadActionText =>
        ShowAdvancedVad ? "Hide timing overrides" : "Advanced timing overrides";

    public bool ShowAdvancedRealtime
    {
        get => _showAdvancedRealtime;
        set
        {
            if (!SetProperty(ref _showAdvancedRealtime, value))
                return;
            OnPropertyChanged(nameof(AdvancedRealtimeActionText));
        }
    }

    public string AdvancedRealtimeActionText =>
        ShowAdvancedRealtime
            ? "Hide realtime overrides"
            : "Advanced realtime overrides";

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (!SetProperty(ref _isDirty, value))
                return;
            OnPropertyChanged(nameof(HasUnsavedChanges));
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasUnsavedChanges => IsDirty;

    public bool IsRuntimeActive
    {
        get => _isRuntimeActive;
        set
        {
            if (!SetProperty(ref _isRuntimeActive, value))
                return;
            OnPropertyChanged(nameof(SaveActionText));
            ToggleMicrophoneTestCommand.NotifyCanExecuteChanged();
        }
    }

    public string SaveActionText =>
        IsRuntimeActive ? "Save & Restart" : "Save";

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (!SetProperty(ref _isSaving, value))
                return;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsRefreshingDevices
    {
        get => _isRefreshingDevices;
        private set
        {
            if (!SetProperty(ref _isRefreshingDevices, value))
                return;
            RefreshDevicesCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsMicrophoneTesting
    {
        get => _isMicrophoneTesting;
        private set
        {
            if (!SetProperty(ref _isMicrophoneTesting, value))
                return;
            OnPropertyChanged(nameof(MicrophoneTestActionText));
            ToggleMicrophoneTestCommand.NotifyCanExecuteChanged();
        }
    }

    public string MicrophoneTestActionText =>
        IsMicrophoneTesting ? "Stop test" : "Test microphone";

    public AudioVisualFrame? MicrophoneTestFrame =>
        _microphoneTest.LatestFrame;

    public string Feedback
    {
        get => _feedback;
        private set
        {
            if (SetProperty(ref _feedback, value))
                OnPropertyChanged(nameof(HasFeedback));
        }
    }

    public bool HasFeedback => Feedback.Length > 0;

    public bool FeedbackIsError
    {
        get => _feedbackIsError;
        private set => SetProperty(ref _feedbackIsError, value);
    }

    public bool HasValidationIssues => ValidationIssues.Count > 0;
    public string ConfigPath => _services.Configuration.ConfigPath;
    public string ApplicationVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ??
        "development";
    public string SanitizedDiagnostics =>
        _services.Configuration.BuildSanitizedDiagnostics(
            _services.Bootstrap,
            ApplicationVersion);

    public DesktopPreferences ApplyTo(DesktopPreferences current) =>
        current with
        {
            Appearance = Appearance,
            ReducedMotion = ReducedMotion,
            LaunchOnLivePage = true,
            SelectedPage = DesktopPage.Live
        };

    public void UpdateRuntimeState(RuntimeState state) =>
        IsRuntimeActive = state is
            RuntimeState.Starting or
            RuntimeState.Running or
            RuntimeState.Stopping;

    public void RequestClose()
    {
        if (IsDirty)
        {
            SetFeedback(
                "Save or discard your changes before closing settings.",
                true);
            return;
        }
        CloseRequested?.Invoke();
    }

    public void ReplaceConfiguration(FoxTransConfig config)
    {
        _originalConfig = config;
        LoadFrom(config);
    }

    public void SetFeedback(string text, bool isError = false)
    {
        FeedbackIsError = isError;
        Feedback = text;
    }

    private bool CanSave() => !IsSaving;

    private async Task SaveAsync()
    {
        ClearValidation();
        if (!TryBuildConfig(out FoxTransConfig? config))
        {
            SetFeedback("Correct the highlighted values before saving.", true);
            SelectedSection = UsesVad && HasVadTimingError ||
                IsVoxtralLlm && HasRealtimeTimingError
                ? SettingsSection.Audio
                : SettingsSection.Providers;
            return;
        }

        IsSaving = true;
        bool restart = IsRuntimeActive;
        FoxTransConfig validConfig = config!;
        try
        {
            await StopMicrophoneTestAsync();
            SetFeedback("Validating configuration");
            DesktopConfigurationSaveResult result = await Task.Run(
                () => _services.Configuration.Save(validConfig));
            if (!result.IsSuccess)
            {
                ApplyIssues(result.Issues);
                SetFeedback(
                    result.Error ??
                    result.Issues.FirstOrDefault()?.Message ??
                    "Configuration could not be saved.",
                    true);
                return;
            }

            _originalConfig = validConfig;
            IsDirty = false;
            SetFeedback(restart
                ? "Configuration saved · Restarting"
                : "Configuration saved");
            await _configurationSaved(result, restart);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void Discard()
    {
        _ = StopMicrophoneTestAsync();
        LoadFrom(_originalConfig);
        SetFeedback("");
        CloseRequested?.Invoke();
    }

    private async Task RefreshDevicesAsync()
    {
        IsRefreshingDevices = true;
        try
        {
            string selected =
                SelectedAudioDevice?.ConfigValue ??
                _originalConfig.EffectiveAudio.Device;
            IReadOnlyList<AudioInputDevice> devices = await Task.Run(
                _services.Configuration.GetAudioInputs);
            PopulateAudioDevices(devices, selected);
            SetFeedback(devices.Count == 0
                ? "No microphone input devices are available."
                : $"Found {devices.Count} microphone input device" +
                  (devices.Count == 1 ? "" : "s") + ".");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            AudioDeviceSelectionException)
        {
            SetFeedback(Safe(exception.Message), true);
        }
        finally
        {
            IsRefreshingDevices = false;
        }
    }

    private bool CanToggleMicrophoneTest() =>
        !IsRuntimeActive &&
        !IsSaving;

    private async Task ToggleMicrophoneTestAsync()
    {
        if (IsMicrophoneTesting)
        {
            await StopMicrophoneTestAsync();
            SetFeedback("Microphone test stopped");
            return;
        }

        try
        {
            IReadOnlyList<AudioInputDevice> devices =
                _services.Configuration.GetAudioInputs();
            ResolvedAudioInput input = AudioDeviceSelection.Resolve(
                SelectedAudioDevice?.ConfigValue ?? "default",
                devices,
                DesktopAudioFormat);
            await _microphoneTest.StartAsync(input);
            IsMicrophoneTesting = true;
            SetFeedback("Microphone test is active · Provider requests are off");
        }
        catch (Exception exception) when (
            exception is AudioDeviceSelectionException or
            InvalidOperationException)
        {
            IsMicrophoneTesting = false;
            SetFeedback(Safe(exception.Message), true);
        }
    }

    private async Task StopMicrophoneTestAsync()
    {
        if (!IsMicrophoneTesting && !_microphoneTest.IsRunning)
            return;
        await _microphoneTest.StopAsync();
        IsMicrophoneTesting = false;
    }

    private void LoadFrom(FoxTransConfig config)
    {
        _suppressDirty = true;
        try
        {
            PipelineConfig pipeline = config.EffectivePipeline;
            _mode = pipeline.Speech switch
            {
                OpenAiTranscriptionConfig =>
                    DesktopPipelineMode.WhisperLlm,
                VoxtralFoxConfig =>
                    DesktopPipelineMode.VoxtralLlm,
                _ =>
                    DesktopPipelineMode.AudioLlm
            };

            OpenAiChatAudioConfig direct =
                pipeline.Speech as OpenAiChatAudioConfig ??
                (OpenAiChatAudioConfig)AppConfig.Default()
                    .EffectivePipeline.Speech!;
            var transcription =
                pipeline.Speech as OpenAiTranscriptionConfig ??
                new OpenAiTranscriptionConfig(
                    "https://openrouter.ai/api/v1",
                    "env:OPENROUTER_API_KEY",
                    "openai/whisper-large-v3",
                    "",
                    "json");
            var voxtral = pipeline.Speech as VoxtralFoxConfig ??
                new VoxtralFoxConfig(
                    "http://127.0.0.1:8080",
                    "env:VOXTRAL_API_KEY",
                    240);
            var translator = pipeline.Translation as OpenAiChatConfig ??
                new OpenAiChatConfig(
                    "https://openrouter.ai/api/v1",
                    "env:OPENROUTER_API_KEY",
                    "",
                    "Translate the source text to English. Reply only with the translation.");

            AudioLlm = new(direct);
            Transcription = new(transcription);
            Voxtral = new(voxtral);
            Translator = new(translator);
            SubscribeProvider(AudioLlm);
            SubscribeProvider(Transcription);
            SubscribeProvider(Voxtral);
            SubscribeProvider(Translator);
            OnPropertyChanged(nameof(AudioLlm));
            OnPropertyChanged(nameof(Transcription));
            OnPropertyChanged(nameof(Voxtral));
            OnPropertyChanged(nameof(Translator));

            WebRtcVadConfig vad =
                pipeline.Vad as WebRtcVadConfig ?? new();
            _vadPreset = vad.Preset;
            _vadStartAfterMs = Optional(vad.StartAfterMs);
            _vadStopAfterMs = Optional(vad.StopAfterMs);
            _vadPreRollMs = Optional(vad.PreRollMs);
            _vadMinimumPhraseMs = Optional(vad.MinimumPhraseMs);

            global::RealtimeConfig realtime =
                pipeline.Realtime ?? new();
            _realtimePreset = realtime.Preset;
            _realtimeMinimumIntervalMs =
                Optional(realtime.MinimumIntervalMs);
            _realtimeMaximumIntervalMs =
                Optional(realtime.MaximumIntervalMs);
            _realtimeMinimumChangedWords =
                Optional(realtime.MinimumChangedWords);
            _realtimeNewUtteranceAfterMs =
                Optional(realtime.NewUtteranceAfterMs);
            _realtimeMaxSourceCharacters =
                Optional(realtime.MaxSourceCharacters);

            PopulateAudioDevices(
                _services.Configuration.GetAudioInputs(),
                config.EffectiveAudio.Device);

            Outputs.Clear();
            foreach (VrChatOscConfig output in config.EffectiveOutputs
                .OfType<VrChatOscConfig>())
            {
                Outputs.Add(CreateOutput(output));
            }
            if (Outputs.Count == 0)
                Outputs.Add(CreateOutput(new()));

            _selectedSection = SettingsSection.Pipeline;
            ClearValidation();
            RaiseAllEditorProperties();
            IsDirty = false;
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    private void SubscribeProvider(ProviderEditorViewModel provider) =>
        provider.Changed += MarkDirty;

    private void PopulateAudioDevices(
        IReadOnlyList<AudioInputDevice> devices,
        string configured)
    {
        AudioDevices.Clear();
        AudioDevices.Add(new("default", "System default microphone"));
        foreach (AudioInputDevice device in devices)
        {
            AudioDevices.Add(new(
                device.DeviceNumber.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                device.DisplayName));
        }

        AudioDeviceOption? selected = AudioDevices.FirstOrDefault(option =>
            string.Equals(
                option.ConfigValue,
                configured,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                option.DisplayName,
                configured,
                StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            selected = new(
                configured,
                configured + " · unavailable",
                false);
            AudioDevices.Add(selected);
        }
        _selectedAudioDevice = selected;
        OnPropertyChanged(nameof(SelectedAudioDevice));
    }

    private void AddOutput()
    {
        int port = 9000 + Outputs.Count;
        Outputs.Add(CreateOutput(new($"127.0.0.1:{port}", true)));
        MarkDirty();
    }

    private OutputEditorViewModel CreateOutput(VrChatOscConfig config) =>
        new(config, RemoveOutput, MoveOutput, MarkDirty);

    private void RemoveOutput(OutputEditorViewModel output)
    {
        Outputs.Remove(output);
        MarkDirty();
    }

    private void MoveOutput(OutputEditorViewModel output, int delta)
    {
        int current = Outputs.IndexOf(output);
        int next = current + delta;
        if (current < 0 || next < 0 || next >= Outputs.Count)
            return;
        Outputs.Move(current, next);
        MarkDirty();
    }

    private bool TryBuildConfig(out FoxTransConfig? config)
    {
        config = null;
        bool validVadStart =
            TryOptional(VadStartAfterMs, out int? vadStart);
        bool validVadStop =
            TryOptional(VadStopAfterMs, out int? vadStop);
        bool validVadPre =
            TryOptional(VadPreRollMs, out int? vadPre);
        bool validVadMinimum =
            TryOptional(VadMinimumPhraseMs, out int? vadMinimum);
        if (!validVadStart ||
            !validVadStop ||
            !validVadPre ||
            !validVadMinimum)
        {
            VadTimingError =
                "Timing overrides must be whole milliseconds or left empty.";
        }

        bool validRealtimeMinimum = TryOptional(
            RealtimeMinimumIntervalMs,
            out int? realtimeMinimum);
        bool validRealtimeMaximum = TryOptional(
            RealtimeMaximumIntervalMs,
            out int? realtimeMaximum);
        bool validRealtimeWords = TryOptional(
            RealtimeMinimumChangedWords,
            out int? realtimeWords);
        bool validRealtimeUtterance = TryOptional(
            RealtimeNewUtteranceAfterMs,
            out int? realtimeUtterance);
        bool validRealtimeCharacters = TryOptional(
            RealtimeMaxSourceCharacters,
            out int? realtimeCharacters);
        if (!validRealtimeMinimum ||
            !validRealtimeMaximum ||
            !validRealtimeWords ||
            !validRealtimeUtterance ||
            !validRealtimeCharacters)
        {
            RealtimeTimingError =
                "Realtime overrides must be whole numbers or left empty.";
        }

        if (HasVadTimingError || HasRealtimeTimingError)
            return false;

        var snapshot = new ConfigurationEditorSnapshot(
            Mode,
            SelectedAudioDevice?.ConfigValue ?? "default",
            VadPreset,
            vadStart,
            vadStop,
            vadPre,
            vadMinimum,
            AudioLlm,
            Transcription,
            Voxtral,
            Translator,
            RealtimePreset,
            realtimeMinimum,
            realtimeMaximum,
            realtimeWords,
            realtimeUtterance,
            realtimeCharacters,
            Outputs.ToArray());
        config = snapshot.ToConfig();
        DesktopConfigurationSaveResult validation =
            _services.Configuration.Validate(config);
        if (!validation.IsSuccess)
        {
            ApplyIssues(validation.Issues);
            return false;
        }
        return true;
    }

    private void ApplyIssues(IReadOnlyList<ConfigIssue> issues)
    {
        ValidationIssues.Clear();
        foreach (ConfigIssue issue in issues)
        {
            string message = Safe(issue.Message);
            ValidationIssues.Add(new(
                issue.Path,
                message,
                issue.Suggestion is null ? null : Safe(issue.Suggestion)));
            switch (issue.Path)
            {
                case "pipeline.speech.baseUrl":
                    ActiveSpeechProvider().SetEndpointError(message);
                    break;
                case "pipeline.speech.model":
                    if (IsAudioLlm)
                        AudioLlm.SetModelError(message);
                    else
                        Transcription.SetModelError(message);
                    break;
                case "pipeline.speech.prompt":
                    AudioLlm.SetPromptError(message);
                    break;
                case "pipeline.speech.apiKey":
                    ActiveSpeechProvider().SetCredentialError(message);
                    break;
                case "pipeline.speech.requestFormat":
                    Transcription.SetRequestFormatError(message);
                    break;
                case "pipeline.speech.delayMs":
                    Voxtral.SetDelayError(message);
                    break;
                case "pipeline.translation.baseUrl":
                    Translator.SetEndpointError(message);
                    break;
                case "pipeline.translation.model":
                    Translator.SetModelError(message);
                    break;
                case "pipeline.translation.prompt":
                    Translator.SetPromptError(message);
                    break;
                case "pipeline.translation.apiKey":
                    Translator.SetCredentialError(message);
                    break;
                case "pipeline.vad.preset":
                    VadTimingError = message;
                    break;
                case "pipeline.realtime.preset":
                case "pipeline.realtime.minimumIntervalMs":
                case "pipeline.realtime.maximumIntervalMs":
                case "pipeline.realtime.minimumChangedWords":
                case "pipeline.realtime.newUtteranceAfterMs":
                case "pipeline.realtime.maxSourceCharacters":
                    RealtimeTimingError = message;
                    break;
                case "outputs":
                    break;
                default:
                    if (issue.Path.StartsWith(
                            "outputs",
                            StringComparison.Ordinal) &&
                        Outputs.Count > 0)
                    {
                        int index = OutputIndex(issue.Path);
                        Outputs[
                            Math.Clamp(index, 0, Outputs.Count - 1)]
                            .SetAddressError(message);
                    }
                    break;
            }
        }
        OnPropertyChanged(nameof(HasValidationIssues));
    }

    private ProviderEditorViewModel ActiveSpeechProvider() => Mode switch
    {
        DesktopPipelineMode.AudioLlm => AudioLlm,
        DesktopPipelineMode.WhisperLlm => Transcription,
        _ => Voxtral
    };

    private void ClearValidation()
    {
        ValidationIssues.Clear();
        AudioLlm?.ClearErrors();
        Transcription?.ClearErrors();
        Voxtral?.ClearErrors();
        Translator?.ClearErrors();
        foreach (OutputEditorViewModel output in Outputs)
            output.SetAddressError(null);
        VadTimingError = "";
        RealtimeTimingError = "";
        OnPropertyChanged(nameof(HasValidationIssues));
    }

    private void MarkDirty()
    {
        if (_suppressDirty)
            return;
        IsDirty = true;
        Feedback = "";
    }

    private void SetTiming(ref string field, string value)
    {
        if (SetProperty(ref field, value))
            MarkDirty();
    }

    private void RaiseSectionVisibility()
    {
        OnPropertyChanged(nameof(IsPipelineSection));
        OnPropertyChanged(nameof(IsAudioSection));
        OnPropertyChanged(nameof(IsProvidersSection));
        OnPropertyChanged(nameof(IsOutputSection));
        OnPropertyChanged(nameof(IsAppearanceSection));
        OnPropertyChanged(nameof(IsAdvancedSection));
    }

    private void RaiseAllEditorProperties()
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsAudioLlm));
        OnPropertyChanged(nameof(IsWhisperLlm));
        OnPropertyChanged(nameof(IsVoxtralLlm));
        OnPropertyChanged(nameof(UsesVad));
        OnPropertyChanged(nameof(UsesTranslator));
        OnPropertyChanged(nameof(PipelineDisplayName));
        OnPropertyChanged(nameof(PipelineDescription));
        OnPropertyChanged(nameof(PipelineFlowSummary));
        OnPropertyChanged(nameof(VadPreset));
        OnPropertyChanged(nameof(VadStartAfterMs));
        OnPropertyChanged(nameof(VadStopAfterMs));
        OnPropertyChanged(nameof(VadPreRollMs));
        OnPropertyChanged(nameof(VadMinimumPhraseMs));
        OnPropertyChanged(nameof(RealtimePreset));
        OnPropertyChanged(nameof(RealtimeMinimumIntervalMs));
        OnPropertyChanged(nameof(RealtimeMaximumIntervalMs));
        OnPropertyChanged(nameof(RealtimeMinimumChangedWords));
        OnPropertyChanged(nameof(RealtimeNewUtteranceAfterMs));
        OnPropertyChanged(nameof(RealtimeMaxSourceCharacters));
        RaiseSectionVisibility();
    }

    private void OpenConfigFolder() =>
        OpenShell(Path.GetDirectoryName(ConfigPath) ?? _services.WorkingDirectory);

    private void OpenRawConfig() =>
        OpenShell(ConfigPath);

    private void OpenShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            SetFeedback(Safe(exception.Message), true);
        }
    }

    private static int OutputIndex(string path)
    {
        int open = path.IndexOf('[', StringComparison.Ordinal);
        int close = path.IndexOf(']', StringComparison.Ordinal);
        return open >= 0 &&
               close > open &&
               int.TryParse(path.AsSpan(open + 1, close - open - 1), out int index)
            ? index
            : 0;
    }

    private static string Optional(int? value) =>
        value?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private static bool TryOptional(string value, out int? parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = null;
            return true;
        }
        if (int.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int number))
        {
            parsed = number;
            return true;
        }
        parsed = null;
        return false;
    }

    private static string Safe(string value)
    {
        string safe = value.Replace('\r', ' ').Replace('\n', ' ');
        return safe.Length <= 800 ? safe : safe[..800] + "…";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _microphoneTest.DisposeAsync();
    }
}
