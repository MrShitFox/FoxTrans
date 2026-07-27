using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FoxTrans.Desktop.Models;

namespace FoxTrans.Desktop.ViewModels;

public interface IPipelineStagesViewModel
{
    ObservableCollection<PipelineStageViewModel> Stages { get; }
}

public sealed class LiveStudioViewModel :
    ObservableObject,
    IPipelineStagesViewModel
{
    private readonly PipelineViewDefinition _definition;
    private readonly Dictionary<PipelineNodeId, PipelineEdgeDefinition> _incoming;
    private VoiceVisualizationMode _voiceMode;
    private AudioVisualFrame? _audioFrame;
    private string _statusText = "Ready when you are";
    private string _statusDetail = "Press Start to begin listening";
    private string _sourceState = "Waiting";
    private string _translationState = "Waiting";
    private bool _translationIsPrevious;
    private bool _reducedMotion;
    private double _animationSeconds;

    public LiveStudioViewModel(
        PipelineViewDefinition definition,
        ResolvedExecutionPlan? plan)
    {
        _definition = definition;
        Plan = plan;
        MicrophoneName = plan?.Audio.DisplayName ?? "Microphone unavailable";
        _incoming = definition.Edges
            .GroupBy(edge => edge.To)
            .ToDictionary(group => group.Key, group => group.First());
        Stages = new ObservableCollection<PipelineStageViewModel>(
            definition.Nodes.Select(node => new PipelineStageViewModel(
                node,
                _incoming.GetValueOrDefault(node.Id))));
        if (plan?.PipelineKind == PipelineKind.DirectAudioTranslation)
        {
            Source.SetTarget(
                "This direct pipeline does not create a source transcript.",
                true,
                DateTimeOffset.UtcNow);
            SourceState = "Not produced";
        }
    }

    public ResolvedExecutionPlan? Plan { get; }
    public string MicrophoneName { get; }
    public ObservableCollection<PipelineStageViewModel> Stages { get; }
    public StreamingTextViewModel Source { get; } = new();
    public StreamingTextViewModel Translation { get; } = new();

    public VoiceVisualizationMode VoiceMode
    {
        get => _voiceMode;
        private set => SetProperty(ref _voiceMode, value);
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

    public string SourceState
    {
        get => _sourceState;
        private set => SetProperty(ref _sourceState, value);
    }

    public string TranslationState
    {
        get => _translationState;
        private set => SetProperty(ref _translationState, value);
    }

    public bool TranslationIsPrevious
    {
        get => _translationIsPrevious;
        private set => SetProperty(ref _translationIsPrevious, value);
    }

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

    public void Apply(
        DesktopRuntimeSnapshot snapshot,
        AudioVisualFrame? audioFrame,
        DateTimeOffset now,
        double animationSeconds)
    {
        VoiceMode = snapshot.VoiceMode;
        AudioFrame = audioFrame;
        AnimationSeconds = animationSeconds;
        (StatusText, StatusDetail) = Status(snapshot);
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDetail));

        PipelineTuiState state = snapshot.Pipeline;
        if (Plan?.PipelineKind != PipelineKind.DirectAudioTranslation)
        {
            string sourceText = state.Source.Text == "None" ? "" : state.Source.Text;
            bool settled = state.Nodes.TryGetValue(
                new("logical-utterance"),
                out PipelineNodeState? logical) &&
                logical.Status == PipelineNodeStatus.Settled;
            Source.SetTarget(sourceText, settled, now);
            SourceState = string.IsNullOrWhiteSpace(sourceText)
                ? "Waiting"
                : settled ? "Settled" : "Live";
        }

        string translationText =
            state.Translation.Text == "None" ? "" : state.Translation.Text;
        Translation.SetTarget(
            translationText,
            state.Translation.IsForCurrentSource,
            now);
        TranslationIsPrevious = !state.Translation.IsForCurrentSource &&
            !string.IsNullOrWhiteSpace(translationText);
        TranslationState = string.IsNullOrWhiteSpace(translationText)
            ? "Waiting"
            : TranslationIsPrevious ? "Previous result" : "Current";

        foreach (PipelineStageViewModel stage in Stages)
        {
            if (!state.Nodes.TryGetValue(stage.Id, out PipelineNodeState? node))
                continue;
            PipelineEdgeState? edgeState = _incoming.TryGetValue(
                stage.Id,
                out PipelineEdgeDefinition? edge)
                ? state.Edges.GetValueOrDefault(edge.Id)
                : null;
            stage.Apply(
                node,
                edgeState,
                now,
                animationSeconds,
                ReducedMotion);
        }
    }

    public void AdvanceText(DateTimeOffset now)
    {
        Source.Advance(now, ReducedMotion);
        Translation.Advance(now, ReducedMotion);
    }

    private static (string Title, string Detail) Status(
        DesktopRuntimeSnapshot snapshot) => snapshot.VoiceMode switch
    {
        VoiceVisualizationMode.Idle =>
            ("Ready when you are", "Press Start to begin listening"),
        VoiceVisualizationMode.Listening =>
            ("Listening", "Speak naturally — FoxTrans is ready"),
        VoiceVisualizationMode.Speech =>
            ("I hear you", "Capturing the current phrase"),
        VoiceVisualizationMode.Processing =>
            ("Translating", "Speech is moving through the pipeline"),
        VoiceVisualizationMode.Success =>
            ("Delivered", "The latest translation is ready"),
        VoiceVisualizationMode.Error =>
            ("Needs attention", snapshot.Notification?.Detail ??
                "The pipeline stopped with an error"),
        VoiceVisualizationMode.Stopping =>
            ("Stopping safely", "Releasing microphone and network resources"),
        _ => ("FoxTrans", "")
    };
}
