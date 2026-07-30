using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FoxTrans.Desktop.ViewModels;

public enum DesktopPipelineMode
{
    AudioLlm,
    WhisperLlm,
    VoxtralLlm
}

public enum CredentialStorageMode
{
    EnvironmentVariable,
    InlineValue
}

public enum SettingsSection
{
    Pipeline,
    Audio,
    Providers,
    Output,
    Appearance,
    VrOverlay,
    Advanced
}

public sealed record AudioDeviceOption(
    string ConfigValue,
    string DisplayName,
    bool IsAvailable = true)
{
    public override string ToString() => DisplayName;
}

public sealed class CredentialEditorViewModel : ObservableObject
{
    private CredentialStorageMode _mode;
    private string _environmentVariable = "";
    private string _inlineValue = "";
    private bool _isRevealed;
    private string _error = "";

    public CredentialEditorViewModel(string? configuredValue)
    {
        Load(configuredValue);
    }

    public CredentialStorageMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value))
                return;
            OnPropertyChanged(nameof(UsesEnvironmentVariable));
            OnPropertyChanged(nameof(UsesInlineValue));
            OnPropertyChanged(nameof(IsMasked));
            OnPropertyChanged(nameof(EnvironmentModeSelected));
            OnPropertyChanged(nameof(InlineModeSelected));
            Changed?.Invoke();
        }
    }

    public string EnvironmentVariable
    {
        get => _environmentVariable;
        set
        {
            if (SetProperty(ref _environmentVariable, value))
                Changed?.Invoke();
        }
    }

    public string InlineValue
    {
        get => _inlineValue;
        set
        {
            if (SetProperty(ref _inlineValue, value))
                Changed?.Invoke();
        }
    }

    public bool IsRevealed
    {
        get => _isRevealed;
        set
        {
            if (SetProperty(ref _isRevealed, value))
            {
                OnPropertyChanged(nameof(RevealActionText));
                OnPropertyChanged(nameof(IsMasked));
            }
        }
    }

    public string RevealActionText => IsRevealed ? "Hide" : "Reveal";
    public bool UsesEnvironmentVariable =>
        Mode == CredentialStorageMode.EnvironmentVariable;
    public bool UsesInlineValue => Mode == CredentialStorageMode.InlineValue;
    public bool IsMasked => UsesInlineValue && !IsRevealed;

    public bool EnvironmentModeSelected
    {
        get => UsesEnvironmentVariable;
        set
        {
            if (value)
                Mode = CredentialStorageMode.EnvironmentVariable;
        }
    }

    public bool InlineModeSelected
    {
        get => UsesInlineValue;
        set
        {
            if (value)
                Mode = CredentialStorageMode.InlineValue;
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public event Action? Changed;

    public string? ToConfiguredValue()
    {
        if (UsesEnvironmentVariable)
        {
            string name = EnvironmentVariable.Trim();
            return name.Length == 0 ? null : "env:" + name;
        }

        return string.IsNullOrWhiteSpace(InlineValue)
            ? null
            : InlineValue;
    }

    public void SetError(string? error) => Error = error ?? "";

    private void Load(string? configuredValue)
    {
        if (!string.IsNullOrWhiteSpace(configuredValue) &&
            configuredValue.StartsWith("env:", StringComparison.Ordinal))
        {
            _mode = CredentialStorageMode.EnvironmentVariable;
            _environmentVariable = configuredValue[4..];
            _inlineValue = "";
        }
        else
        {
            _mode = CredentialStorageMode.InlineValue;
            _environmentVariable = "";
            _inlineValue = configuredValue ?? "";
        }
    }
}

public abstract class ProviderEditorViewModel : ObservableObject
{
    private string _endpoint;
    private string _endpointError = "";
    private string _credentialError = "";

    protected ProviderEditorViewModel(
        string? endpoint,
        string? credential)
    {
        _endpoint = endpoint ?? "";
        Credential = new(credential);
        Credential.Changed += () => Changed?.Invoke();
    }

    public string Endpoint
    {
        get => _endpoint;
        set
        {
            if (SetProperty(ref _endpoint, value))
                Changed?.Invoke();
        }
    }

    public CredentialEditorViewModel Credential { get; }

    public string EndpointError
    {
        get => _endpointError;
        private set
        {
            if (SetProperty(ref _endpointError, value))
                OnPropertyChanged(nameof(HasEndpointError));
        }
    }

    public string CredentialError
    {
        get => _credentialError;
        private set
        {
            if (SetProperty(ref _credentialError, value))
                OnPropertyChanged(nameof(HasCredentialError));
        }
    }

    public bool HasEndpointError => EndpointError.Length > 0;
    public bool HasCredentialError => CredentialError.Length > 0;
    public event Action? Changed;

    public virtual void ClearErrors()
    {
        EndpointError = "";
        CredentialError = "";
        Credential.SetError(null);
    }

    public void SetEndpointError(string? value) => EndpointError = value ?? "";

    public void SetCredentialError(string? value)
    {
        CredentialError = value ?? "";
        Credential.SetError(value);
    }

    protected void RaiseChanged() => Changed?.Invoke();
}

public abstract class PromptProviderEditorViewModel :
    ProviderEditorViewModel
{
    private string _model;
    private string _prompt;
    private string _modelError = "";
    private string _promptError = "";

    protected PromptProviderEditorViewModel(
        string? baseUrl,
        string? apiKey,
        string? model,
        string? prompt,
        string defaultPrompt)
        : base(baseUrl, apiKey)
    {
        _model = model ?? "";
        _prompt = prompt ?? defaultPrompt;
    }

    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, value))
                RaiseChanged();
        }
    }

    public string Prompt
    {
        get => _prompt;
        set
        {
            if (SetProperty(ref _prompt, value))
                RaiseChanged();
        }
    }

    public string ModelError
    {
        get => _modelError;
        private set
        {
            if (SetProperty(ref _modelError, value))
                OnPropertyChanged(nameof(HasModelError));
        }
    }

    public string PromptError
    {
        get => _promptError;
        private set
        {
            if (SetProperty(ref _promptError, value))
                OnPropertyChanged(nameof(HasPromptError));
        }
    }

    public bool HasModelError => ModelError.Length > 0;
    public bool HasPromptError => PromptError.Length > 0;

    public override void ClearErrors()
    {
        base.ClearErrors();
        ModelError = "";
        PromptError = "";
    }

    public void SetModelError(string? value) => ModelError = value ?? "";
    public void SetPromptError(string? value) => PromptError = value ?? "";
}

public sealed class AudioLlmProviderEditorViewModel :
    PromptProviderEditorViewModel
{
    public AudioLlmProviderEditorViewModel(OpenAiChatAudioConfig? config)
        : base(
            config?.BaseUrl,
            config?.ApiKey,
            config?.Model,
            config?.Prompt,
            "Translate this audio to English. Reply only with the translated text.")
    {
    }
}

public sealed class TranscriptionProviderEditorViewModel : ProviderEditorViewModel
{
    private string _model;
    private string _language;
    private string _requestFormat;
    private string _modelError = "";
    private string _requestFormatError = "";

    public TranscriptionProviderEditorViewModel(
        OpenAiTranscriptionConfig? config)
        : base(config?.BaseUrl, config?.ApiKey)
    {
        _model = config?.Model ?? "";
        _language = config?.Language ?? "";
        _requestFormat = config?.RequestFormat ?? "multipart";
    }

    public IReadOnlyList<string> RequestFormats { get; } =
        ["multipart", "json"];

    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, value))
                RaiseChanged();
        }
    }

    public string Language
    {
        get => _language;
        set
        {
            if (SetProperty(ref _language, value))
                RaiseChanged();
        }
    }

    public string RequestFormat
    {
        get => _requestFormat;
        set
        {
            if (SetProperty(ref _requestFormat, value))
                RaiseChanged();
        }
    }

    public string ModelError
    {
        get => _modelError;
        private set
        {
            if (SetProperty(ref _modelError, value))
                OnPropertyChanged(nameof(HasModelError));
        }
    }

    public string RequestFormatError
    {
        get => _requestFormatError;
        private set
        {
            if (SetProperty(ref _requestFormatError, value))
                OnPropertyChanged(nameof(HasRequestFormatError));
        }
    }

    public bool HasModelError => ModelError.Length > 0;
    public bool HasRequestFormatError => RequestFormatError.Length > 0;

    public override void ClearErrors()
    {
        base.ClearErrors();
        ModelError = "";
        RequestFormatError = "";
    }

    public void SetModelError(string? value) => ModelError = value ?? "";
    public void SetRequestFormatError(string? value) =>
        RequestFormatError = value ?? "";
}

public sealed class TranslatorProviderEditorViewModel :
    PromptProviderEditorViewModel
{
    public TranslatorProviderEditorViewModel(OpenAiChatConfig? config)
        : base(
            config?.BaseUrl,
            config?.ApiKey,
            config?.Model,
            config?.Prompt,
            "Translate the source text to English. Reply only with the translation.")
    {
    }
}

public sealed class VoxtralProviderEditorViewModel : ProviderEditorViewModel
{
    private int _delayMs;
    private string _delayError = "";

    public VoxtralProviderEditorViewModel(VoxtralFoxConfig? config)
        : base(config?.BaseUrl, config?.ApiKey)
    {
        _delayMs = config?.DelayMs ?? 240;
    }

    public IReadOnlyList<int> Delays { get; } =
        ConfigValidator.VoxtralDelays.Order().ToArray();

    public int DelayMs
    {
        get => _delayMs;
        set
        {
            if (SetProperty(ref _delayMs, value))
                RaiseChanged();
        }
    }

    public string DelayError
    {
        get => _delayError;
        private set
        {
            if (SetProperty(ref _delayError, value))
                OnPropertyChanged(nameof(HasDelayError));
        }
    }

    public bool HasDelayError => DelayError.Length > 0;

    public override void ClearErrors()
    {
        base.ClearErrors();
        DelayError = "";
    }

    public void SetDelayError(string? value) => DelayError = value ?? "";
}

public sealed class OutputEditorViewModel : ObservableObject
{
    private readonly Action<OutputEditorViewModel> _remove;
    private readonly Action<OutputEditorViewModel, int> _move;
    private readonly Action _changed;
    private bool _isEnabled = true;
    private string _address;
    private bool _typingIndicator;
    private string _addressError = "";

    public OutputEditorViewModel(
        VrChatOscConfig config,
        Action<OutputEditorViewModel> remove,
        Action<OutputEditorViewModel, int> move,
        Action changed)
    {
        _address = config.Address ?? "127.0.0.1:9000";
        _typingIndicator = config.TypingIndicator ?? true;
        _remove = remove;
        _move = move;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => _remove(this));
        MoveUpCommand = new RelayCommand(() => _move(this, -1));
        MoveDownCommand = new RelayCommand(() => _move(this, 1));
    }

    public string TypeName => "VRChat OSC";

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
                _changed();
        }
    }

    public string Address
    {
        get => _address;
        set
        {
            if (SetProperty(ref _address, value))
                _changed();
        }
    }

    public bool TypingIndicator
    {
        get => _typingIndicator;
        set
        {
            if (SetProperty(ref _typingIndicator, value))
                _changed();
        }
    }

    public string AddressError
    {
        get => _addressError;
        private set
        {
            if (SetProperty(ref _addressError, value))
                OnPropertyChanged(nameof(HasAddressError));
        }
    }

    public bool HasAddressError => AddressError.Length > 0;
    public IRelayCommand RemoveCommand { get; }
    public IRelayCommand MoveUpCommand { get; }
    public IRelayCommand MoveDownCommand { get; }

    public VrChatOscConfig ToConfig() =>
        new(Address.Trim(), TypingIndicator);

    public void SetAddressError(string? value) =>
        AddressError = value ?? "";
}

public sealed record ConfigurationEditorSnapshot(
    DesktopPipelineMode Mode,
    string AudioDevice,
    string VadPreset,
    int? VadStartAfterMs,
    int? VadStopAfterMs,
    int? VadPreRollMs,
    int? VadMinimumPhraseMs,
    AudioLlmProviderEditorViewModel AudioLlm,
    TranscriptionProviderEditorViewModel Transcription,
    VoxtralProviderEditorViewModel Voxtral,
    TranslatorProviderEditorViewModel Translator,
    string RealtimePreset,
    int? RealtimeMinimumIntervalMs,
    int? RealtimeMaximumIntervalMs,
    int? RealtimeMinimumChangedWords,
    int? RealtimeNewUtteranceAfterMs,
    int? RealtimeMaxSourceCharacters,
    IReadOnlyList<OutputEditorViewModel> Outputs)
{
    public FoxTransConfig ToConfig()
    {
        WebRtcVadConfig? vad = Mode == DesktopPipelineMode.VoxtralLlm
            ? null
            : new(
                VadPreset,
                VadStartAfterMs,
                VadStopAfterMs,
                VadPreRollMs,
                VadMinimumPhraseMs);

        SpeechProviderConfig speech = Mode switch
        {
            DesktopPipelineMode.AudioLlm => new OpenAiChatAudioConfig(
                AudioLlm.Endpoint.Trim(),
                AudioLlm.Credential.ToConfiguredValue(),
                AudioLlm.Model.Trim(),
                AudioLlm.Prompt),
            DesktopPipelineMode.WhisperLlm => new OpenAiTranscriptionConfig(
                Transcription.Endpoint.Trim(),
                Transcription.Credential.ToConfiguredValue(),
                Transcription.Model.Trim(),
                string.IsNullOrWhiteSpace(Transcription.Language)
                    ? null
                    : Transcription.Language.Trim(),
                Transcription.RequestFormat),
            _ => new VoxtralFoxConfig(
                Voxtral.Endpoint.Trim(),
                Voxtral.Credential.ToConfiguredValue(),
                Voxtral.DelayMs)
        };

        TranslationProviderConfig? translation =
            Mode == DesktopPipelineMode.AudioLlm
                ? null
                : new OpenAiChatConfig(
                    Translator.Endpoint.Trim(),
                    Translator.Credential.ToConfiguredValue(),
                    Translator.Model.Trim(),
                    Translator.Prompt);
        RealtimeConfig? realtime = Mode == DesktopPipelineMode.VoxtralLlm
            ? new(
                RealtimePreset,
                RealtimeMinimumIntervalMs,
                RealtimeMaximumIntervalMs,
                RealtimeMinimumChangedWords,
                RealtimeNewUtteranceAfterMs,
                RealtimeMaxSourceCharacters)
            : null;

        return new(
            Audio: new(AudioDevice),
            Pipeline: new(vad, speech, translation, realtime),
            Outputs: Outputs
                .Where(output => output.IsEnabled)
                .Select(output => (OutputProviderConfig)output.ToConfig())
                .ToArray());
    }
}
