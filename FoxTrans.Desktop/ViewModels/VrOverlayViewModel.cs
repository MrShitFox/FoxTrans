using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FoxTrans.Desktop.Models;
using FoxTrans.Desktop.Services;

namespace FoxTrans.Desktop.ViewModels;

/// <summary>
/// Desktop-only projection for the SteamVR surface. It deliberately owns an
/// independent Live Studio projection so both consumers advance the same
/// bridge snapshot without sharing mutable text animation state.
/// </summary>
public sealed class VrOverlayViewModel : ObservableObject
{
    private readonly LiveStudioViewModel _projection;
    private string _pipelineName = "Loading…";
    private string _modelLine = "";
    private string _sourceText = "";
    private string _translationText = "";
    private bool _showHeader = true;
    private bool _showVoiceStatus = true;
    private bool _showRecognition = true;
    private bool _showTranslation = true;
    private long _revision;

    public VrOverlayViewModel(
        PipelineViewDefinition definition,
        ResolvedExecutionPlan? plan,
        bool initializing = false)
    {
        _projection = new LiveStudioViewModel(definition, plan, initializing);
        _projection.PropertyChanged += OnProjectionChanged;
    }

    public StreamingTextViewModel Source => _projection.Source;
    public StreamingTextViewModel Translation => _projection.Translation;
    public string BrandTitle => "FoxTrans Overlay";
    public string SourceText => _sourceText;
    public string TranslationText => _translationText;
    public string OverlayTranslationText =>
        HasTranslationText
            ? TranslationText
            : VoiceMode switch
            {
                VoiceVisualizationMode.Speech => "Listening…",
                VoiceVisualizationMode.Processing => "Translating…",
                VoiceVisualizationMode.Error => "Translation unavailable",
                _ => "Speak to translate"
            };
    public VoiceVisualizationMode VoiceMode => _projection.VoiceMode;
    public AudioVisualFrame? AudioFrame => _projection.AudioFrame;
    public string StatusText => _projection.StatusText;
    public string StatusDetail => _projection.StatusDetail;
    public bool HasSourceText => !string.IsNullOrWhiteSpace(SourceText);
    public bool HasTranslationText => !string.IsNullOrWhiteSpace(TranslationText);
    public bool HasRecognition => _projection.HasRecognition && ShowRecognition;
    public bool IsErrorMode => _projection.IsErrorMode;
    public bool IsSuccessMode => _projection.IsSuccessMode;
    public double AnimationSeconds => _projection.AnimationSeconds;
    public long Revision => _revision;

    public string PipelineName
    {
        get => _pipelineName;
        set => SetAndMark(ref _pipelineName, value);
    }

    public string ModelLine
    {
        get => _modelLine;
        set => SetAndMark(ref _modelLine, value);
    }

    public bool ReducedMotion
    {
        get => _projection.ReducedMotion;
        set
        {
            if (_projection.ReducedMotion == value)
                return;
            _projection.ReducedMotion = value;
            OnPropertyChanged();
            MarkChanged();
        }
    }

    public bool ShowHeader
    {
        get => _showHeader;
        set => SetAndMark(ref _showHeader, value);
    }

    public bool ShowVoiceStatus
    {
        get => _showVoiceStatus;
        set => SetAndMark(ref _showVoiceStatus, value);
    }

    public bool ShowRecognition
    {
        get => _showRecognition;
        set
        {
            if (!SetProperty(ref _showRecognition, value))
                return;
            OnPropertyChanged(nameof(HasRecognition));
            MarkChanged();
        }
    }

    public bool ShowTranslation
    {
        get => _showTranslation;
        set => SetAndMark(ref _showTranslation, value);
    }

    public void ApplyPreferences(VrOverlayPreferences preferences)
    {
        ShowHeader = preferences.ShowHeader;
        ShowVoiceStatus = preferences.ShowVoiceStatus;
        ShowRecognition = preferences.ShowRecognition;
        ShowTranslation = preferences.ShowTranslation;
    }

    public void Apply(
        DesktopRuntimeSnapshot snapshot,
        AudioVisualFrame? audioFrame,
        DateTimeOffset now,
        double animationSeconds,
        bool advanceVisuals = true)
    {
        _projection.Apply(
            snapshot,
            audioFrame,
            now,
            animationSeconds,
            advanceVisuals);
        PipelineTuiState state = snapshot.Pipeline;
        SetProjectedText(
            ref _sourceText,
            _projection.HasRecognition && state.Source.Text != "None"
                ? state.Source.Text
                : "",
            nameof(SourceText),
            nameof(HasSourceText));
        SetProjectedText(
            ref _translationText,
            state.Translation.Text != "None" &&
            state.Translation.IsForCurrentSource
                ? VrChatTextFormatter.Format(state.Translation.Text)
                : "",
            nameof(TranslationText),
            nameof(HasTranslationText));
    }

    public void AdvanceText(DateTimeOffset now)
    {
        // The VR HUD projects immutable snapshot text directly. Live Studio's
        // entrance/exit animator is intentionally not part of this data path.
    }

    internal VisualTickMode GetVisualTickMode(DateTimeOffset now)
    {
        if (!ShowVoiceStatus && !ShowRecognition && !ShowTranslation)
            return VisualTickMode.None;
        return _projection.GetVisualTickMode(now);
    }

    private void OnProjectionChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        bool affectsRenderedFrame = true;
        switch (eventArgs.PropertyName)
        {
            case nameof(LiveStudioViewModel.VoiceMode):
                OnPropertyChanged(nameof(VoiceMode));
                OnPropertyChanged(nameof(IsErrorMode));
                OnPropertyChanged(nameof(IsSuccessMode));
                OnPropertyChanged(nameof(OverlayTranslationText));
                break;
            case nameof(LiveStudioViewModel.AudioFrame):
                OnPropertyChanged(nameof(AudioFrame));
                break;
            case nameof(LiveStudioViewModel.StatusText):
                OnPropertyChanged(nameof(StatusText));
                break;
            case nameof(LiveStudioViewModel.StatusDetail):
                OnPropertyChanged(nameof(StatusDetail));
                break;
            case nameof(LiveStudioViewModel.HasRecognition):
                OnPropertyChanged(nameof(HasRecognition));
                break;
            case nameof(LiveStudioViewModel.AnimationSeconds):
                OnPropertyChanged(nameof(AnimationSeconds));
                break;
            default:
                affectsRenderedFrame = false;
                break;
        }
        if (affectsRenderedFrame)
            MarkChanged();
    }

    private void SetProjectedText(
        ref string field,
        string value,
        string propertyName,
        string visibilityPropertyName)
    {
        if (!SetProperty(ref field, value, propertyName))
            return;
        OnPropertyChanged(visibilityPropertyName);
        if (propertyName == nameof(TranslationText))
            OnPropertyChanged(nameof(OverlayTranslationText));
        MarkChanged();
    }

    private void SetAndMark<T>(ref T field, T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
            MarkChanged();
    }

    private void MarkChanged()
    {
        _revision = _revision == long.MaxValue ? 1 : _revision + 1;
        OnPropertyChanged(nameof(Revision));
    }
}
