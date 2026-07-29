using FoxTrans.Desktop.Models;
using FoxTrans.Desktop.ViewModels;
using Xunit;

public sealed class LiveStudioViewModelTests
{
    private static readonly DateTimeOffset Start =
        DateTimeOffset.UnixEpoch;

    [Fact]
    public void NewBatchUtteranceDoesNotReplayPreviousText()
    {
        ResolvedExecutionPlan plan = DesktopTestPlans.Create(
            PipelineKind.BatchTranscriptionTranslation);
        PipelineViewDefinition definition =
            PipelineTopologyBuilder.Build(plan);
        DesktopRuntimeSnapshot snapshot =
            DesktopRuntimeSnapshot.Create(definition, Start);
        var studio = new LiveStudioViewModel(definition, plan)
        {
            ReducedMotion = true
        };

        snapshot = Reduce(
            snapshot,
            AppEvent.TranscriptionCompleted("same source"),
            1);
        snapshot = Reduce(
            snapshot,
            AppEvent.TranslationCompleted("same translation"),
            2);
        Apply(studio, snapshot, 2);

        Assert.Equal("same source", studio.Source.DisplayedText);
        Assert.Equal(
            "same translation",
            studio.Translation.DisplayedText);

        snapshot = Reduce(snapshot, AppEvent.Listening(), 3);
        Apply(studio, snapshot, 3);
        studio.ReducedMotion = false;
        snapshot = Reduce(snapshot, AppEvent.SpeechStarted(), 4);
        Apply(studio, snapshot, 4);
        studio.AdvanceText(
            At(4) + StreamingTextViewModel.UtteranceExitDuration);

        Assert.Empty(studio.Source.DisplayedText);
        Assert.Empty(studio.Translation.DisplayedText);

        Apply(studio, snapshot, 5);

        Assert.Empty(studio.Source.DisplayedText);
        Assert.Empty(studio.Translation.DisplayedText);
        Assert.False(studio.HasSourceText);
        Assert.False(studio.HasTranslationText);

        studio.ReducedMotion = true;
        snapshot = Reduce(
            snapshot,
            AppEvent.TranscriptionCompleted("same source"),
            6);
        Apply(studio, snapshot, 6);

        Assert.Equal("same source", studio.Source.DisplayedText);
        Assert.Empty(studio.Translation.DisplayedText);

        snapshot = Reduce(
            snapshot,
            AppEvent.TranslationCompleted("same translation"),
            7);
        Apply(studio, snapshot, 7);

        Assert.Equal(
            "same translation",
            studio.Translation.DisplayedText);
    }

    [Fact]
    public void FreshAudioUsesActiveCadenceAndStaleAudioStopsIt()
    {
        ResolvedExecutionPlan plan = DesktopTestPlans.Create(
            PipelineKind.BatchTranscriptionTranslation);
        PipelineViewDefinition definition =
            PipelineTopologyBuilder.Build(plan);
        DesktopRuntimeSnapshot snapshot =
            DesktopRuntimeSnapshot.Create(definition, Start);
        var studio = new LiveStudioViewModel(definition, plan);
        var frame = new AudioVisualFrame(
            0.24f,
            0.42f,
            false,
            true,
            new float[Pcm16AudioFeatureExtractor.SpectrumBandCount],
            42,
            Start);

        studio.Apply(
            snapshot,
            frame,
            Start,
            0,
            advanceVisuals: false);

        Assert.Equal(
            VisualTickMode.Active,
            studio.GetVisualTickMode(Start));
        Assert.Equal(
            VisualTickMode.None,
            studio.GetVisualTickMode(
                Start + TimeSpan.FromMilliseconds(121)));
    }

    [Fact]
    public void ProcessingAndSuccessEffectsUseLowCadenceOnlyWhileAnimated()
    {
        ResolvedExecutionPlan plan = DesktopTestPlans.Create(
            PipelineKind.BatchTranscriptionTranslation);
        PipelineViewDefinition definition =
            PipelineTopologyBuilder.Build(plan);
        DesktopRuntimeSnapshot snapshot =
            DesktopRuntimeSnapshot.Create(definition, Start);
        var studio = new LiveStudioViewModel(definition, plan);

        studio.Apply(
            snapshot with { VoiceMode = VoiceVisualizationMode.Processing },
            null,
            Start,
            0,
            advanceVisuals: false);
        Assert.Equal(
            VisualTickMode.Processing,
            studio.GetVisualTickMode(Start + TimeSpan.FromSeconds(5)));

        studio.Apply(
            snapshot with { VoiceMode = VoiceVisualizationMode.Success },
            null,
            Start + TimeSpan.FromSeconds(6),
            6,
            advanceVisuals: false);
        Assert.Equal(
            VisualTickMode.Processing,
            studio.GetVisualTickMode(Start + TimeSpan.FromSeconds(6)));
        Assert.Equal(
            VisualTickMode.None,
            studio.GetVisualTickMode(
                Start + TimeSpan.FromSeconds(6) +
                VoiceWaveformAnimationModel.SuccessPulseDuration +
                TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void ApplyingAudioRaisesTheWaveformBindingProperties()
    {
        ResolvedExecutionPlan plan = DesktopTestPlans.Create(
            PipelineKind.BatchTranscriptionTranslation);
        PipelineViewDefinition definition =
            PipelineTopologyBuilder.Build(plan);
        DesktopRuntimeSnapshot snapshot =
            DesktopRuntimeSnapshot.Create(definition, Start) with
            {
                VoiceMode = VoiceVisualizationMode.Speech
            };
        var studio = new LiveStudioViewModel(definition, plan);
        var changed = new List<string?>();
        studio.PropertyChanged += (_, eventArgs) =>
            changed.Add(eventArgs.PropertyName);

        studio.Apply(
            snapshot,
            new AudioVisualFrame(
                0.24f,
                0.42f,
                false,
                true,
                new float[Pcm16AudioFeatureExtractor.SpectrumBandCount],
                1,
                Start),
            Start,
            1,
            advanceVisuals: false);

        Assert.Contains(nameof(LiveStudioViewModel.VoiceMode), changed);
        Assert.Contains(nameof(LiveStudioViewModel.AudioFrame), changed);
        Assert.Contains(nameof(LiveStudioViewModel.AnimationSeconds), changed);
    }

    private static DesktopRuntimeSnapshot Reduce(
        DesktopRuntimeSnapshot snapshot,
        AppEvent appEvent,
        int seconds) =>
        DesktopRuntimeReducer.Reduce(snapshot, appEvent, At(seconds));

    private static void Apply(
        LiveStudioViewModel studio,
        DesktopRuntimeSnapshot snapshot,
        int seconds)
    {
        DateTimeOffset now = At(seconds);
        studio.Apply(snapshot, null, now, seconds);
        studio.AdvanceText(now);
    }

    private static DateTimeOffset At(int seconds) =>
        Start + TimeSpan.FromSeconds(seconds);
}
