using CommunityToolkit.Mvvm.ComponentModel;
using FoxTrans.Desktop.Models;

namespace FoxTrans.Desktop.ViewModels;

public sealed class LiveStudioViewModel : ObservableObject
{
    private VoiceVisualizationMode _voiceMode;
    private VoiceVisualizationMode _previousMode;
    private AudioVisualFrame? _audioFrame;
    private string _statusText = "Ready";
    private string _statusDetail = "Press Start when you are ready";
    private bool _hasSourceText;
    private bool _hasTranslationText;
    private bool _reducedMotion;
    private double _animationSeconds;
    private long _sourceEpoch;
    private long _sourceUtterance;
    private bool _hasSourceIdentity;
    private bool _hasRecognition;
    private bool _sourceAwaitsNewGeneration;
    private bool _translationAwaitsNewGeneration;
    private long _sourceGenerationAtTransition;
    private long _translationGenerationAtTransition;

    public LiveStudioViewModel(
        PipelineViewDefinition definition,
        ResolvedExecutionPlan? plan,
        bool initializing = false)
    {
        Plan = plan;
        if (initializing)
        {
            _statusText = "Loading";
            _statusDetail = "Preparing configuration";
        }
        _hasRecognition =
            plan?.PipelineKind != PipelineKind.DirectAudioTranslation;
    }

    public ResolvedExecutionPlan? Plan { get; }
    public bool HasRecognition
    {
        get => _hasRecognition;
        private set => SetProperty(ref _hasRecognition, value);
    }
    public StreamingTextViewModel Source { get; } = new();
    public StreamingTextViewModel Translation { get; } = new();

    public VoiceVisualizationMode VoiceMode
    {
        get => _voiceMode;
        private set
        {
            if (!SetProperty(ref _voiceMode, value))
                return;
            OnPropertyChanged(nameof(IsErrorMode));
            OnPropertyChanged(nameof(IsSuccessMode));
        }
    }

    public AudioVisualFrame? AudioFrame
    {
        get => _audioFrame;
        private set => SetProperty(ref _audioFrame, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public bool HasSourceText
    {
        get => _hasSourceText;
        private set
        {
            if (!SetProperty(ref _hasSourceText, value))
                return;
            OnPropertyChanged(nameof(IsSourceEmpty));
        }
    }

    public bool IsSourceEmpty => !HasSourceText;

    public bool HasTranslationText
    {
        get => _hasTranslationText;
        private set
        {
            if (!SetProperty(ref _hasTranslationText, value))
                return;
            OnPropertyChanged(nameof(IsTranslationEmpty));
        }
    }

    public bool IsTranslationEmpty => !HasTranslationText;

    public bool ReducedMotion
    {
        get => _reducedMotion;
        set => SetProperty(ref _reducedMotion, value);
    }

    public double AnimationSeconds
    {
        get => _animationSeconds;
        private set => SetProperty(ref _animationSeconds, value);
    }

    public bool IsErrorMode => VoiceMode == VoiceVisualizationMode.Error;
    public bool IsSuccessMode => VoiceMode == VoiceVisualizationMode.Success;

    public void Apply(
        DesktopRuntimeSnapshot snapshot,
        AudioVisualFrame? audioFrame,
        DateTimeOffset now,
        double animationSeconds)
    {
        PipelineTuiState state = snapshot.Pipeline;
        BeginUtteranceTransitionIfNeeded(snapshot, state, now);
        VoiceMode = snapshot.VoiceMode;
        AudioFrame = audioFrame;
        AnimationSeconds = animationSeconds;
        (StatusText, StatusDetail) = Status(snapshot);

        if (HasRecognition)
        {
            string sourceText =
                state.Source.Text == "None" ? "" : state.Source.Text;
            if (_sourceAwaitsNewGeneration)
            {
                if (SourceGeneration(state) !=
                    _sourceGenerationAtTransition)
                {
                    _sourceAwaitsNewGeneration = false;
                }
                else
                {
                    sourceText = "";
                }
            }
            bool settled = state.Nodes.TryGetValue(
                new("logical-utterance"),
                out PipelineNodeState? logical) &&
                logical.Status == PipelineNodeStatus.Settled;
            Source.SetTarget(sourceText, settled, now);
            HasSourceText = HasVisibleText(Source, sourceText);
        }

        string translationText =
            state.Translation.Text == "None" ||
            !state.Translation.IsForCurrentSource
                ? ""
                : state.Translation.Text;
        if (_translationAwaitsNewGeneration)
        {
            if (state.Statistics.TranslationsAccepted !=
                _translationGenerationAtTransition)
            {
                _translationAwaitsNewGeneration = false;
            }
            else
            {
                translationText = "";
            }
        }
        Translation.SetTarget(
            translationText,
            state.Translation.IsForCurrentSource,
            now);
        HasTranslationText = HasVisibleText(
            Translation,
            translationText);
        _previousMode = snapshot.VoiceMode;
    }

    public void AdvanceText(DateTimeOffset now)
    {
        Source.Advance(now, ReducedMotion);
        Translation.Advance(now, ReducedMotion);
    }

    public void ApplyPreview(
        VoiceVisualizationMode mode,
        AudioVisualFrame? audioFrame,
        string source,
        string translation,
        bool showRecognition,
        DateTimeOffset now,
        double animationSeconds)
    {
        HasRecognition = showRecognition;
        VoiceMode = mode;
        AudioFrame = audioFrame;
        AnimationSeconds = animationSeconds;
        (StatusText, StatusDetail) = mode switch
        {
            VoiceVisualizationMode.Idle =>
                ("Ready", "Press Start when you are ready"),
            VoiceVisualizationMode.Listening =>
                ("Listening", "The microphone is open"),
            VoiceVisualizationMode.Speech =>
                ("Hearing you", "Listening to the current phrase"),
            VoiceVisualizationMode.Processing =>
                ("Translating", "Preparing the current translation"),
            VoiceVisualizationMode.Success =>
                ("Translation ready", "The latest phrase was delivered"),
            VoiceVisualizationMode.Error =>
                ("Needs attention", "The provider needs attention"),
            VoiceVisualizationMode.Stopping =>
                ("Stopping", "Releasing the microphone"),
            _ => ("Ready", "")
        };

        if (showRecognition)
        {
            Source.SetTarget(source, true, now);
            HasSourceText = !string.IsNullOrWhiteSpace(source);
        }
        Translation.SetTarget(translation, true, now);
        HasTranslationText = !string.IsNullOrWhiteSpace(translation);
        DateTimeOffset settledAt =
            now + StreamingTextAnimator.FinalSettlementBound;
        Source.Advance(settledAt, ReducedMotion);
        Translation.Advance(settledAt, ReducedMotion);
        DateTimeOffset fullyRevealedAt =
            settledAt + StreamingTextAnimator.ElementRevealDuration;
        Source.Advance(fullyRevealedAt, ReducedMotion);
        Translation.Advance(fullyRevealedAt, ReducedMotion);
    }

    private void BeginUtteranceTransitionIfNeeded(
        DesktopRuntimeSnapshot snapshot,
        PipelineTuiState state,
        DateTimeOffset now)
    {
        if (Plan?.PipelineKind ==
            PipelineKind.RealtimeTranscriptionTranslation)
        {
            bool hasIdentity =
                state.Source.Epoch != 0 ||
                state.Source.Utterance != 0;
            bool changed = hasIdentity &&
                _hasSourceIdentity &&
                (state.Source.Epoch != _sourceEpoch ||
                 state.Source.Utterance != _sourceUtterance);
            if (changed)
            {
                Source.BeginNewUtterance(now);
                Translation.BeginNewUtterance(now);
            }
            if (hasIdentity)
            {
                _hasSourceIdentity = true;
                _sourceEpoch = state.Source.Epoch;
                _sourceUtterance = state.Source.Utterance;
            }
            return;
        }

        if (snapshot.VoiceMode == VoiceVisualizationMode.Speech &&
            _previousMode != VoiceVisualizationMode.Speech)
        {
            if (HasRecognition)
            {
                _sourceGenerationAtTransition =
                    SourceGeneration(state);
                _sourceAwaitsNewGeneration = true;
                Source.BeginNewUtterance(now);
            }
            _translationGenerationAtTransition =
                state.Statistics.TranslationsAccepted;
            _translationAwaitsNewGeneration = true;
            Translation.BeginNewUtterance(now);
        }
    }

    private static long SourceGeneration(PipelineTuiState state) =>
        state.Nodes.TryGetValue(
            new("batch-stt"),
            out PipelineNodeState? source)
            ? source.SuccessCount
            : 0;

    private static bool HasVisibleText(
        StreamingTextViewModel presenter,
        string currentText) =>
        !string.IsNullOrWhiteSpace(currentText) ||
        presenter.IsExiting ||
        !string.IsNullOrWhiteSpace(presenter.DisplayedText);

    private static (string Title, string Detail) Status(
        DesktopRuntimeSnapshot snapshot)
    {
        if (snapshot.Pipeline.Nodes.Values.Any(
                node => node.Status == PipelineNodeStatus.Reconnecting))
        {
            return ("Reconnecting", "Restoring the realtime connection");
        }

        if (snapshot.VoiceMode == VoiceVisualizationMode.Processing)
        {
            if (snapshot.Pipeline.Nodes.TryGetValue(
                    new("batch-stt"),
                    out PipelineNodeState? transcription) &&
                transcription.Status == PipelineNodeStatus.Active)
            {
                return ("Transcribing", "Turning speech into text");
            }
            if (snapshot.Pipeline.Nodes.Values.Any(
                    node => node.Status == PipelineNodeStatus.Publishing))
            {
                return ("Sending to VRChat", "Delivering the translation");
            }
            return ("Translating", "Preparing the current translation");
        }

        return snapshot.VoiceMode switch
        {
            VoiceVisualizationMode.Idle =>
                ("Ready", "Press Start when you are ready"),
            VoiceVisualizationMode.Listening =>
                ("Listening", "The microphone is open"),
            VoiceVisualizationMode.Speech =>
                ("Hearing you", "Listening to the current phrase"),
            VoiceVisualizationMode.Success =>
                ("Translation ready", "The latest phrase was delivered"),
            VoiceVisualizationMode.Error =>
                ("Needs attention", snapshot.Notification?.Detail ??
                    "The pipeline stopped with an error"),
            VoiceVisualizationMode.Stopping =>
                ("Stopping", "Releasing the microphone"),
            _ => ("Ready", "")
        };
    }
}
