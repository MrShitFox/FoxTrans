using FoxTrans.Desktop.Models;
using FoxTrans.Desktop.Services;
using Xunit;

public sealed class DesktopReducerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(PipelineKind.DirectAudioTranslation, "audio-llm")]
    [InlineData(PipelineKind.BatchTranscriptionTranslation, "batch-stt")]
    [InlineData(PipelineKind.RealtimeTranscriptionTranslation, "streaming-stt")]
    public void ResolvedTopologyAndEveryPrimaryStageRemainTyped(
        PipelineKind kind,
        string primaryStage)
    {
        PipelineViewDefinition definition =
            PipelineTopologyBuilder.Build(DesktopTestPlans.Create(kind, 2));
        DesktopRuntimeSnapshot state =
            DesktopRuntimeSnapshot.Create(definition, Start);

        Assert.Contains(definition.Nodes, node => node.Id.Value == "audio-input");
        Assert.Contains(definition.Nodes, node => node.Id.Value == primaryStage);
        Assert.Equal(2, definition.Nodes.Count(node => node.Kind == PipelineNodeKind.Output));
        Assert.Equal(
            RuntimeState.Stopped,
            state.RuntimeState);
    }

    [Fact]
    public void RuntimeAndVoiceModesFollowTypedLifecycle()
    {
        DesktopRuntimeSnapshot state = State(PipelineKind.BatchTranscriptionTranslation);

        state = Reduce(state, AppEvent.RuntimeStateChanged(RuntimeState.Starting, 1), 1);
        Assert.Equal(RuntimeState.Starting, state.RuntimeState);
        Assert.Equal(VoiceVisualizationMode.Processing, state.VoiceMode);

        state = Reduce(state, AppEvent.RuntimeStateChanged(RuntimeState.Running, 1), 2);
        state = Reduce(state, AppEvent.Listening(), 3);
        Assert.Equal(VoiceVisualizationMode.Listening, state.VoiceMode);

        state = Reduce(state, AppEvent.SpeechStarted(), 4);
        Assert.Equal(VoiceVisualizationMode.Speech, state.VoiceMode);

        state = Reduce(state, AppEvent.TranscriptionStarted(), 5);
        Assert.Equal(VoiceVisualizationMode.Processing, state.VoiceMode);

        state = Reduce(state, AppEvent.TranslationCompleted("ready"), 6);
        Assert.Equal(VoiceVisualizationMode.Success, state.VoiceMode);
        state = DesktopRuntimeReducer.Advance(
            state,
            Start + TimeSpan.FromSeconds(2));
        Assert.Equal(VoiceVisualizationMode.Listening, state.VoiceMode);
    }

    [Fact]
    public void ClassicSourceTranslationAndPreviousResultSemanticsArePreserved()
    {
        DesktopRuntimeSnapshot state = State(PipelineKind.BatchTranscriptionTranslation);
        state = Reduce(state, AppEvent.TranscriptionCompleted("исходный текст"), 1);
        state = Reduce(state, AppEvent.TranslationCompleted("translated text"), 2);

        Assert.Equal("исходный текст", state.Pipeline.Source.Text);
        Assert.Equal("translated text", state.Pipeline.Translation.Text);
        Assert.True(state.Pipeline.Translation.IsForCurrentSource);
    }

    [Fact]
    public void RealtimeEpochUtteranceRevisionAndRecoveryStayCorrelated()
    {
        DesktopRuntimeSnapshot state = State(PipelineKind.RealtimeTranscriptionTranslation);
        TranslationCandidate first = Candidate(1, 1, 1, "first source");
        state = Reduce(state, AppEvent.LogicalUtteranceStarted(first), 1);
        state = Reduce(state, AppEvent.RealtimeTranslationAccepted(first, "first result"), 2);
        TranslationCandidate next = Candidate(1, 2, 1, "next source");
        state = Reduce(state, AppEvent.LogicalUtteranceStarted(next), 3);

        Assert.Equal("next source", state.Pipeline.Source.Text);
        Assert.Equal("first result", state.Pipeline.Translation.Text);
        Assert.False(state.Pipeline.Translation.IsForCurrentSource);

        state = Reduce(state, AppEvent.TranscriptEpochResynchronized("reset"), 4);
        Assert.Equal("None", state.Pipeline.Source.Text);
        Assert.Equal("None", state.Pipeline.Translation.Text);

        state = Reduce(
            state,
            AppEvent.VoxtralReconnectScheduled(new(1, TimeSpan.FromSeconds(1))),
            5);
        Assert.Equal(
            PipelineNodeStatus.Reconnecting,
            state.Pipeline.Nodes[new("streaming-stt")].Status);
        state = Reduce(state, AppEvent.VoxtralReconnected(new(2)), 6);
        Assert.Equal(VoiceVisualizationMode.Listening, state.VoiceMode);
    }

    [Fact]
    public void OutputTimeoutQuarantineFailureAndRecoveryAreVisible()
    {
        DesktopRuntimeSnapshot state = State(PipelineKind.RealtimeTranscriptionTranslation);
        var timeout = new RealtimeOutputTelemetry(
            RealtimeOutputTelemetryKind.TimedOut,
            "VRChat OSC",
            1,
            1,
            1,
            10,
            Timeout: TimeSpan.FromSeconds(2),
            OutputIndex: 0);
        state = Reduce(state, AppEvent.RealtimeOutput(timeout), 1);
        Assert.Equal(
            PipelineNodeStatus.TimedOut,
            state.Pipeline.Nodes[new("output:1")].Status);
        Assert.NotNull(state.Notification);

        var quarantine = timeout with
        {
            Kind = RealtimeOutputTelemetryKind.Quarantined,
            OperationSequence = 11
        };
        state = Reduce(state, AppEvent.RealtimeOutput(quarantine), 2);
        Assert.Equal(
            PipelineNodeStatus.Quarantined,
            state.Pipeline.Nodes[new("output:1")].Status);

        state = Reduce(
            state,
            AppEvent.RuntimeStateChanged(RuntimeState.Faulted, 1, "provider"),
            3);
        state = Reduce(state, AppEvent.FatalError("provider unavailable"), 4);
        Assert.Equal(RuntimeState.Faulted, state.RuntimeState);
        Assert.Equal(VoiceVisualizationMode.Error, state.VoiceMode);

        state = Reduce(
            state,
            AppEvent.RuntimeStateChanged(RuntimeState.Running, 2),
            5);
        Assert.Equal(RuntimeState.Running, state.RuntimeState);
        Assert.Equal(VoiceVisualizationMode.Listening, state.VoiceMode);
    }

    [Fact]
    public void StaleOperationCompletionCannotOverwriteNewerOperation()
    {
        DesktopRuntimeSnapshot state = State(PipelineKind.BatchTranscriptionTranslation);
        state = Reduce(state, AppEvent.RuntimeTelemetry(new StageOperationTelemetry(
            new("batch-stt"), 2, StageOperationPhase.Started)), 1);
        state = Reduce(state, AppEvent.RuntimeTelemetry(new StageOperationTelemetry(
            new("batch-stt"), 1, StageOperationPhase.Completed)), 2);

        PipelineNodeState node = state.Pipeline.Nodes[new("batch-stt")];
        Assert.Equal(PipelineNodeStatus.Active, node.Status);
        Assert.Equal("2", node.WorkIdentity);
    }

    [Fact]
    public async Task TenThousandPartialsRemainBoundedAndConvergeToLatest()
    {
        PipelineViewDefinition definition = PipelineTopologyBuilder.Build(
            DesktopTestPlans.Create(PipelineKind.RealtimeTranscriptionTranslation));
        await using var bridge = new DesktopEventBridge(
            definition,
            DesktopTestPlans.Create(PipelineKind.RealtimeTranscriptionTranslation));
        TranslationCandidate candidate = Candidate(1, 1, 1, "source 0");
        bridge.Report(AppEvent.LogicalUtteranceStarted(candidate));
        for (int index = 1; index <= 10_000; index++)
        {
            bridge.Report(AppEvent.LogicalUtteranceUpdated(
                candidate with
                {
                    Revision = index,
                    SourceText = $"source {index}"
                }));
        }

        await WaitUntilAsync(
            () => bridge.Snapshot.Pipeline.Source.Text == "source 10000");

        Assert.Equal(DesktopEventBridge.EventCapacity, bridge.MaximumQueuedEvents);
        Assert.True(bridge.DroppedOrCoalescedCount > 0);
        Assert.Equal("source 10000", bridge.Snapshot.Pipeline.Source.Text);
        Assert.InRange(bridge.Snapshot.Pipeline.RecentEvents.Count, 0, 100);
    }

    private static DesktopRuntimeSnapshot State(PipelineKind kind)
    {
        PipelineViewDefinition definition =
            PipelineTopologyBuilder.Build(DesktopTestPlans.Create(kind));
        return DesktopRuntimeSnapshot.Create(definition, Start);
    }

    private static DesktopRuntimeSnapshot Reduce(
        DesktopRuntimeSnapshot state,
        AppEvent appEvent,
        int milliseconds) =>
        DesktopRuntimeReducer.Reduce(
            state,
            appEvent,
            Start + TimeSpan.FromMilliseconds(milliseconds));

    private static TranslationCandidate Candidate(
        long epoch,
        long utterance,
        long revision,
        string source) =>
        new(
            epoch,
            utterance,
            revision,
            source,
            false,
            false,
            revision,
            revision * 80,
            Start,
            Start);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("The bounded reducer did not converge.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
