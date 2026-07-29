using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

public sealed class PipelineTopologyTests
{
    [Fact]
    public void DirectTopologyIsStableAndSanitized()
    {
        PipelineViewDefinition view = PipelineTopologyBuilder.Build(Plans.Direct());

        Assert.Equal(
            ["audio-input", "vad", "audio-llm", "output:1"],
            view.Nodes.Select(node => node.Id.Value));
        Assert.Equal(
            ["audio-input>vad", "vad>audio-llm", "audio-llm>output:1"],
            view.Edges.Select(edge => edge.Id.ToString()));
        Assert.DoesNotContain("direct-secret", Settings(view));
        Assert.DoesNotContain("translate this complete prompt", Settings(view));
        Assert.Contains("Promptconfigured", Settings(view));
    }

    [Fact]
    public void BatchTopologyHasSeparateSttAndTranslator()
    {
        PipelineViewDefinition view = PipelineTopologyBuilder.Build(Plans.Batch());

        Assert.Equal(
            ["audio-input", "vad", "batch-stt", "text-translation", "output:1"],
            view.Nodes.Select(node => node.Id.Value));
        Assert.DoesNotContain(view.Nodes, node => node.Kind == PipelineNodeKind.AudioLlm);
        Assert.DoesNotContain(view.Nodes, node => node.Kind == PipelineNodeKind.StreamingStt);
    }

    [Fact]
    public void RealtimeTopologyHasNoVadAndContainsRealtimeStages()
    {
        PipelineViewDefinition view = PipelineTopologyBuilder.Build(Plans.Realtime());

        Assert.Equal(
            ["audio-input", "streaming-stt", "logical-utterance", "text-translation", "output:1"],
            view.Nodes.Select(node => node.Id.Value));
        Assert.DoesNotContain(view.Nodes, node => node.Kind == PipelineNodeKind.Vad);
        Assert.DoesNotContain(view.Nodes, node => node.Kind == PipelineNodeKind.BatchStt);
        Assert.Contains("responsive", Settings(view));
    }

    [Fact]
    public void MultipleOutputsAreDeterministicFanOut()
    {
        ResolvedExecutionPlan plan = Plans.Batch(outputs: 3);
        PipelineViewDefinition first = PipelineTopologyBuilder.Build(plan);
        PipelineViewDefinition second = PipelineTopologyBuilder.Build(plan);

        Assert.Equal(["output:1", "output:2", "output:3"],
            first.Nodes.Where(node => node.Kind == PipelineNodeKind.Output)
                .Select(node => node.Id.Value));
        Assert.Equal(3, first.Edges.Count(edge => edge.From.Value == "text-translation"));
        Assert.Equal(
            first.Nodes.Select(node => (node.Id, node.Kind, node.Title, node.Subtitle,
                string.Join("|", node.Settings.Select(setting => $"{setting.Name}={setting.Value}")))),
            second.Nodes.Select(node => (node.Id, node.Kind, node.Title, node.Subtitle,
                string.Join("|", node.Settings.Select(setting => $"{setting.Name}={setting.Value}")))));
        Assert.Equal(first.Edges, second.Edges);
    }

    private static string Settings(PipelineViewDefinition definition) =>
        string.Concat(definition.Nodes.SelectMany(node => node.Settings)
            .Select(setting => setting.Name + setting.Value));
}

public sealed class PipelineReducerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ClassicFlowTracksQueueTimingsFailuresAndDelivery()
    {
        PipelineTuiState state = Create(Plans.Batch());
        state = Reduce(state, AppEvent.Listening(), 1);
        state = Reduce(state, AppEvent.SpeechStarted(), 2);
        state = Reduce(state, AppEvent.SegmentCompleted(TimeSpan.FromSeconds(2.6)), 3);
        state = Reduce(state, AppEvent.RuntimeTelemetry(
            new QueueTelemetry(1, 4, 1, 0, false)), 4);
        state = Reduce(state, AppEvent.TranscriptionStarted(), 5);
        state = Reduce(state, AppEvent.TranscriptionCompleted("[red]source[/]") with
        {
            Duration = TimeSpan.FromMilliseconds(120)
        }, 6);
        state = Reduce(state, AppEvent.TextTranslationStarted(), 7);
        state = Reduce(state, AppEvent.TranslationCompleted("result") with
        {
            Duration = TimeSpan.FromMilliseconds(80)
        }, 8);
        state = Reduce(state, AppEvent.OutputDelivered(new OutputDeliveryTelemetry(
            new("output:1"), "VRChat OSC", 1, TranslationUpdateKind.Translation,
            OutputDeliveryPhase.Delivered, TimeSpan.FromMilliseconds(2))), 9);

        Assert.Equal("[red]source[/]", state.Source.Text);
        Assert.Equal("result", state.Translation.Text);
        Assert.Equal(1, state.Statistics.SpeechSegmentsCompleted);
        Assert.Equal(1, state.Statistics.TranslationsDelivered);
        Assert.Equal(1, state.Nodes[new("batch-stt")].QueueCount);
        Assert.Equal(TimeSpan.FromMilliseconds(120),
            state.Nodes[new("batch-stt")].LastDuration);
        Assert.Equal(PipelineNodeStatus.Ready, state.Nodes[new("output:1")].Status);
    }

    [Fact]
    public void RealtimePresentationRetainsPreviousThenEpochResetClearsIt()
    {
        PipelineTuiState state = Create(Plans.Realtime());
        TranslationCandidate first = Candidate(1, 1, "first");
        state = Reduce(state, AppEvent.LogicalUtteranceStarted(first), 1);
        state = Reduce(state, AppEvent.RealtimeTranslationAccepted(first, "translated"), 2);
        state = Reduce(state, AppEvent.LogicalUtteranceSettled(first with { IsSettled = true }), 3);
        state = Reduce(state, AppEvent.LogicalUtteranceStarted(Candidate(2, 1, "next")), 4);

        Assert.Equal("next", state.Source.Text);
        Assert.Equal("translated", state.Translation.Text);
        Assert.False(state.Translation.IsForCurrentSource);

        state = Reduce(state, AppEvent.TranscriptEpochResynchronized("reset"), 5);
        Assert.Equal("None", state.Source.Text);
        Assert.Equal("None", state.Translation.Text);
        Assert.True(state.Translation.IsForCurrentSource);
    }

    [Fact]
    public void HistoryIsBoundedAndWarningsCoalesce()
    {
        PipelineTuiState state = Create(Plans.Direct());
        for (int index = 0; index < 150; index++)
            state = PipelineTuiReducer.Reduce(
                state,
                AppEvent.ConfigWarning($"warning {index}"),
                Start.AddMilliseconds(index * 10));
        Assert.Equal(100, state.RecentEvents.Count);

        state = PipelineTuiReducer.Reduce(state, AppEvent.VoxtralWarning(
            new("busy", "repeat", null, null, null, null, null, null, null, null)), Start.AddSeconds(2));
        state = PipelineTuiReducer.Reduce(state, AppEvent.VoxtralWarning(
            new("busy", "repeat", null, null, null, null, null, null, null, null)), Start.AddSeconds(3));
        Assert.Equal(2, state.RecentEvents[^1].RepeatCount);
    }

    [Fact]
    public void ShutdownAndFatalErrorRemainVisible()
    {
        PipelineTuiState state = Create(Plans.Direct());
        state = Reduce(state, AppEvent.FatalError("controlled fatal"), 1);
        Assert.True(state.IsStopping);
        Assert.Contains(state.RecentEvents, item =>
            item.Severity == UiEventSeverity.Error &&
            item.Message.Contains("controlled fatal"));
        state = Reduce(state, AppEvent.Stopped(), 2);
        Assert.All(state.Nodes.Values, node => Assert.Equal(PipelineNodeStatus.Stopped, node.Status));
    }

    [Fact]
    public void StaleTypedOperationCannotRegressNewerState()
    {
        PipelineTuiState state = Create(Plans.Batch());
        state = Reduce(state, AppEvent.RuntimeTelemetry(new StageOperationTelemetry(
            new("batch-stt"), 12, StageOperationPhase.Completed,
            TimeSpan.FromMilliseconds(42))), 1);
        state = Reduce(state, AppEvent.RuntimeTelemetry(new StageOperationTelemetry(
            new("batch-stt"), 11, StageOperationPhase.Started)), 2);

        PipelineNodeState stt = state.Nodes[new("batch-stt")];
        Assert.Equal(PipelineNodeStatus.Ready, stt.Status);
        Assert.Equal("12", stt.WorkIdentity);
        Assert.Equal(TimeSpan.FromMilliseconds(42), stt.LastDuration);
    }

    private static PipelineTuiState Create(ResolvedExecutionPlan plan) =>
        PipelineTuiState.Create(PipelineTopologyBuilder.Build(plan), Start);

    private static PipelineTuiState Reduce(PipelineTuiState state, AppEvent item, int ms) =>
        PipelineTuiReducer.Reduce(state, item, Start.AddMilliseconds(ms));

    private static TranslationCandidate Candidate(long utterance, long revision, string source) =>
        new(1, utterance, revision, source, false, false, revision, revision * 80, Start, Start);
}

public sealed class AudioMeterTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public async Task SilenceAndFullScaleAreMeasuredWithoutChangingFrames()
    {
        byte[] silence = new byte[640];
        byte[] loud = new byte[640];
        for (int index = 0; index < loud.Length; index += 2)
        {
            loud[index] = 0xff;
            loud[index + 1] = 0x7f;
        }
        byte[] originalLoud = loud.ToArray();
        var reporter = new MeterReporter();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var source = new MeteredAudioSource(
            new MeterSource([new(silence, Format), new(loud, Format)]),
            reporter,
            () =>
            {
                DateTimeOffset result = now;
                now += TimeSpan.FromMilliseconds(100);
                return result;
            });

        var observed = new List<AudioFrame>();
        await foreach (AudioFrame frame in source.ReadFramesAsync(TestContext.Current.CancellationToken))
            observed.Add(frame);

        AudioLevelTelemetry[] meters = reporter.Events
            .Select(item => item.Telemetry).OfType<AudioLevelTelemetry>().ToArray();
        Assert.Equal(-96, meters[0].RmsDb);
        Assert.True(meters[1].PeakDb > -0.01);
        Assert.True(meters[1].IsClipping);
        Assert.Equal(originalLoud, loud);
        Assert.Equal(loud, observed[1].Pcm.ToArray());
    }

    [Fact]
    public async Task UnsupportedFormatReportsUnavailableAndRateIsThrottled()
    {
        var format = new AudioFormat(16000, 8, 1);
        var frames = Enumerable.Range(0, 20)
            .Select(_ => new AudioFrame(new byte[320], format)).ToArray();
        var reporter = new MeterReporter();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var source = new MeteredAudioSource(
            new MeterSource(frames),
            reporter,
            () =>
            {
                DateTimeOffset result = now;
                now += TimeSpan.FromMilliseconds(20);
                return result;
            });
        await foreach (AudioFrame _ in source.ReadFramesAsync(TestContext.Current.CancellationToken)) { }

        AudioLevelTelemetry[] meters = reporter.Events.Select(item => item.Telemetry)
            .OfType<AudioLevelTelemetry>().ToArray();
        Assert.InRange(meters.Length, 3, 5);
        Assert.All(meters, meter => Assert.False(meter.IsAvailable));
        Assert.InRange(meters[^1].FramesObserved, 15, 20);
    }

    private sealed class MeterReporter : IAppReporter
    {
        public List<AppEvent> Events { get; } = [];
        public void Report(AppEvent appEvent) => Events.Add(appEvent);
    }

    private sealed class MeterSource(IReadOnlyList<AudioFrame> frames) : IAudioSource
    {
        public AudioFormat Format => frames[0].Format;
        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (AudioFrame frame in frames)
                yield return frame;
        }
    }
}

public sealed class QueueTelemetryTests
{
    [Fact]
    public async Task OwnedQueueReportsExactBoundsBackpressureAndShutdownCleanup()
    {
        var reporter = new QueueReporter();
        var translator = new BlockingAudioTranslator(reporter.Backpressure.Task);
        await FoxTransApp.RunDirectAudioPipelineAsync(
            new QueueSource(Enumerable.Range(0, 6).Select(_ =>
                new AudioFrame(new byte[640], new(16000, 16, 1))).ToArray()),
            new EveryFrameSegmenter(),
            translator,
            [],
            reporter,
            cancellationToken: TestContext.Current.CancellationToken);

        QueueTelemetry[] telemetry = reporter.Events
            .Select(item => item.Telemetry).OfType<QueueTelemetry>().ToArray();
        Assert.Equal((0, 4), (telemetry[0].Count, telemetry[0].Capacity));
        Assert.Contains(telemetry, item => item.Count == 4 && item.Backpressured);
        Assert.Equal(0, telemetry[^1].Count);
        Assert.All(telemetry, item => Assert.InRange(item.Count, 0, item.Capacity));
        Assert.True(telemetry[^1].Produced >= telemetry[^1].Consumed);
    }

    private sealed class QueueReporter : IAppReporter
    {
        public ConcurrentQueue<AppEvent> Events { get; } = new();
        public TaskCompletionSource Backpressure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(AppEvent appEvent)
        {
            Events.Enqueue(appEvent);
            if (appEvent.Telemetry is QueueTelemetry { Backpressured: true })
                Backpressure.TrySetResult();
        }
    }

    private sealed class BlockingAudioTranslator(Task release) : IAudioTranslator
    {
        private int _calls;
        public async Task<string> TranslateAsync(
            AudioSegment segment,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                await release.WaitAsync(cancellationToken);
            return "translated";
        }
    }

    private sealed class QueueSource(IReadOnlyList<AudioFrame> frames) : IAudioSource
    {
        public AudioFormat Format => new(16000, 16, 1);
        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (AudioFrame frame in frames)
                yield return frame;
        }
    }

    private sealed class EveryFrameSegmenter : IAudioSegmenter
    {
        public async IAsyncEnumerable<SegmentationUpdate> SegmentAsync(
            IAsyncEnumerable<AudioFrame> frames,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (AudioFrame frame in frames.WithCancellation(cancellationToken))
                yield return new(
                    SegmentationUpdateKind.SegmentCompleted,
                    new AudioSegment(frame.Pcm, frame.Format),
                    frame.Format.DurationOf(frame.Pcm.Length));
        }
    }
}

public sealed class PipelineRendererTests
{
    public static TheoryData<int, int> Viewports => new()
    {
        { 160, 40 }, { 120, 30 }, { 100, 25 }, { 80, 25 },
        { 70, 20 }, { 50, 15 }, { 30, 8 }
    };

    [Theory]
    [MemberData(nameof(Viewports))]
    public void RepresentativeStatesRenderSafelyAtEveryViewport(int width, int height)
    {
        PipelineTuiState state = PipelineTuiState.Create(
            PipelineTopologyBuilder.Build(Plans.Realtime()), DateTimeOffset.UnixEpoch);
        state = PipelineTuiReducer.Reduce(state,
            AppEvent.LogicalUtteranceStarted(new(
                2, 14, 37,
                "[red]not markup[/] Русский العربية 日本語 emoji 👨‍👩‍👧‍👦 e\u0301",
                false, false, 37, 1000,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)),
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        state = PipelineTuiReducer.Reduce(state,
            AppEvent.RealtimeTranslationAccepted(new(
                2, 14, 37, "source", false, false, 37, 1000,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                "[blue]translated[/]"),
            DateTimeOffset.UnixEpoch.AddSeconds(2));
        state = PipelineTuiReducer.Reduce(state,
            AppEvent.FatalError("visible error"),
            DateTimeOffset.UnixEpoch.AddSeconds(3));

        var console = new TestConsole();
        console.Profile.Width = width;
        console.Profile.Height = height;
        console.Write(new PipelineTuiRenderer().Render(
            state, new(width, height), new(true, false)));
        string output = console.Output;

        Assert.Contains("FoxTrans", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("visible error", output);
        Assert.DoesNotContain("direct-secret", output);
        Assert.DoesNotContain("Authorization", output);
    }

    [Fact]
    public void WideMultipleOutputGraphShowsFanOutAndAsciiStructure()
    {
        PipelineTuiState state = PipelineTuiState.Create(
            PipelineTopologyBuilder.Build(Plans.Batch(outputs: 2)),
            DateTimeOffset.UnixEpoch);
        var console = new TestConsole();
        console.Profile.Width = 160;
        console.Profile.Height = 40;
        console.Write(new PipelineTuiRenderer().Render(
            state, new(160, 40), new(true, false)));

        Assert.Contains("VRCHAT OSC 1", console.Output);
        Assert.Contains("VRCHAT OSC 2", console.Output);
        Assert.DoesNotContain("+->", console.Output);
        Assert.True(console.Output.IndexOf("VRCHAT OSC 1", StringComparison.Ordinal) <
            console.Output.IndexOf("VRCHAT OSC 2", StringComparison.Ordinal));
        Assert.DoesNotContain("translate this complete prompt", console.Output);
    }
}

public sealed class ViewOnlyVerticalRendererTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<int, int> AllViewports => new()
    {
        { 160, 50 }, { 120, 40 }, { 100, 35 }, { 80, 25 },
        { 70, 22 }, { 55, 18 }, { 40, 12 }, { 30, 8 }
    };

    [Theory]
    [MemberData(nameof(AllViewports))]
    public void DirectClassicAndRealtimeRemainVerticalAtEveryViewport(int width, int height)
    {
        foreach (ResolvedExecutionPlan plan in new[]
        {
            Plans.Direct(), Plans.Batch(), Plans.Realtime()
        })
        {
            PipelineViewDefinition definition = PipelineTopologyBuilder.Build(plan);
            PipelineTuiState state = PipelineTuiState.Create(definition, Start);
            var console = new TestConsole();
            console.Profile.Width = width;
            console.Profile.Height = height;
            console.Write(new PipelineTuiRenderer().Render(
                state, new(width, height), new(true, false)));
            string output = console.Output;

            int previous = -1;
            foreach (PipelineNodeDefinition node in definition.Nodes)
            {
                string title = width < 45 || height <= 40
                    ? node.Kind switch
                    {
                        PipelineNodeKind.AudioInput => "MIC",
                        PipelineNodeKind.Vad => "VAD",
                        PipelineNodeKind.BatchStt => "STT",
                        PipelineNodeKind.StreamingStt => "VOXTRAL",
                        PipelineNodeKind.LogicalUtterance => "UTTERANCE",
                        PipelineNodeKind.TextTranslation => "LLM",
                        PipelineNodeKind.Output => node.Title.ToUpperInvariant(),
                        _ => node.Title.ToUpperInvariant()
                    }
                    : node.Title.ToUpperInvariant();
                string marker = $"[{definition.Nodes.ToList().IndexOf(node) + 1}] {title}";
                int current = output.IndexOf(marker, StringComparison.Ordinal);
                Assert.True(current >= 0, $"Missing {title} at {width}x{height}.\n{output}");
                Assert.True(current > previous, $"Topology order changed.\n{output}");
                previous = current;
            }
            Assert.DoesNotContain(" > ", output);
            Assert.DoesNotContain(" -> ", output);
            Assert.Contains("|", output);
            Assert.Contains("v", output);
        }
    }

    [Fact]
    public void MultipleOutputsAreStackedInTopologyOrder()
    {
        PipelineViewDefinition definition = PipelineTopologyBuilder.Build(Plans.Batch(outputs: 3));
        var console = new TestConsole();
        console.Profile.Width = 160;
        console.Profile.Height = 50;
        console.Write(new PipelineTuiRenderer().Render(
            PipelineTuiState.Create(definition, Start),
            new(160, 50),
            new(true, false)));
        string output = console.Output;
        Assert.DoesNotContain("+->", output);
        Assert.True(output.IndexOf("VRCHAT OSC 1", StringComparison.Ordinal) <
            output.IndexOf("VRCHAT OSC 2", StringComparison.Ordinal));
        Assert.True(output.IndexOf("VRCHAT OSC 2", StringComparison.Ordinal) <
            output.IndexOf("VRCHAT OSC 3", StringComparison.Ordinal));
    }

    [Fact]
    public void FullCardsShowResolvedSettingsAndNeverSecrets()
    {
        foreach (ResolvedExecutionPlan plan in new[]
        {
            Plans.Direct(), Plans.Batch(), Plans.Realtime()
        })
        {
            PipelineViewDefinition definition = PipelineTopologyBuilder.Build(plan);
            var console = new TestConsole();
            console.Profile.Width = 160;
            console.Profile.Height = 50;
            console.Write(new PipelineTuiRenderer().Render(
                PipelineTuiState.Create(definition, Start),
                new(160, 50),
                new(true, false)));
            string output = console.Output;
            foreach (PipelineNodeDefinition node in definition.Nodes)
                foreach (PipelineSettingView setting in node.Settings)
                    Assert.Contains(setting.Value, output);
            Assert.DoesNotContain("direct-secret", output);
            Assert.DoesNotContain("translation-secret", output);
            Assert.DoesNotContain("voxtral-secret", output);
            Assert.DoesNotContain("translate this complete prompt", output);
            Assert.DoesNotContain("private prompt", output);
            Assert.DoesNotContain("user:pass", output);
            Assert.DoesNotContain("secret=query", output);
        }
    }

    [Fact]
    public void ActivePulseAndEdgeMarkerArePureFunctionsOfTime()
    {
        PipelineTuiState state = PipelineTuiState.Create(
            PipelineTopologyBuilder.Build(Plans.Batch()), Start);
        state = PipelineTuiReducer.Reduce(state, AppEvent.SpeechStarted(), Start);
        state = PipelineTuiReducer.Reduce(state,
            AppEvent.RuntimeTelemetry(new EdgeActivityTelemetry(
                new("audio-input"), new("vad"), PipelineDataKind.PcmAudio, "frame 12")), Start);
        var renderer = new PipelineTuiRenderer();
        string frame0 = Render(renderer, state, Start);
        string frame1 = Render(renderer, state, Start.AddMilliseconds(250));
        string frameIdle = Render(renderer, state, Start.AddSeconds(2));

        Assert.NotEqual(frame0, frame1);
        Assert.Contains("PCM audio", frame0);
        Assert.Contains("[*]", frame0);
        Assert.NotEqual(frame1, frameIdle);
        Assert.Contains("PCM audio", frameIdle);
    }

    [Fact]
    public void TwoActiveStagesPulseWhileErrorStageDoesNotBecomeGreen()
    {
        PipelineTuiState state = PipelineTuiState.Create(
            PipelineTopologyBuilder.Build(Plans.Realtime()), Start);
        state = PipelineTuiReducer.Reduce(state,
            AppEvent.VoxtralSessionStarted(new(
                "session", 7, "model", 1, 1)), Start);
        state = PipelineTuiReducer.Reduce(state,
            AppEvent.RealtimeTranslationStarted(new(
                1, 1, 1, "source", false, false, 1, 80, Start, Start),
                RealtimeSchedulingDecision.MinimumChangedWords, TimeSpan.Zero),
            Start);
        string first = Render(new PipelineTuiRenderer(), state, Start);
        string second = Render(new PipelineTuiRenderer(), state, Start.AddMilliseconds(200));
        Assert.NotEqual(first, second);
        Assert.Contains("[*]", first);
        Assert.Contains("[+]", second);

        state = PipelineTuiReducer.Reduce(state, AppEvent.ApiError("lost"), Start.AddSeconds(1));
        string error = Render(new PipelineTuiRenderer(), state, Start.AddSeconds(1));
        Assert.Contains("lost", error);
        Assert.DoesNotContain("LIVE [*] ERROR", error);
    }

    [Fact]
    public void CompletionFlashExpiresAndErrorPersistsUntilSuccess()
    {
        PipelineTuiState state = PipelineTuiState.Create(
            PipelineTopologyBuilder.Build(Plans.Batch()), Start);
        state = PipelineTuiReducer.Reduce(state, AppEvent.TranscriptionStarted(), Start);
        state = PipelineTuiReducer.Reduce(state, AppEvent.ApiError("provider failed"), Start.AddSeconds(1));
        string error = Render(new PipelineTuiRenderer(), state, Start.AddSeconds(1));
        Assert.Contains("provider failed", error);

        state = PipelineTuiReducer.Reduce(state, AppEvent.TextTranslationStarted(), Start.AddSeconds(2));
        state = PipelineTuiReducer.Reduce(state,
            AppEvent.TranslationCompleted("done") with { Duration = TimeSpan.FromMilliseconds(23) },
            Start.AddSeconds(3));
        string flash = Render(new PipelineTuiRenderer(), state, Start.AddSeconds(3.5));
        string settled = Render(new PipelineTuiRenderer(), state, Start.AddSeconds(5));
        Assert.Contains("COMPLETED", flash);
        Assert.DoesNotContain("COMPLETED", settled);
        Assert.Contains("23 ms", flash);
    }

    [Fact]
    public void RedirectedStdinDoesNotDisableRichMode()
    {
        UiModeSelection selection = UiModeSelector.Select(
            UiMode.Auto,
            new TerminalDetection(false, true, true, false, new(120, 40)));
        Assert.Equal(UiMode.Rich, selection.EffectiveMode);
    }

    [Fact]
    public void HostSurfaceContainsNoInteractiveInputOrSelectionState()
    {
        Assert.DoesNotContain(typeof(ITerminalEnvironment).GetMethods(),
            method => method.Name.Contains("Read", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(PipelineTuiState).GetProperties(),
            property => property.Name.Contains("Selected", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Panel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(PipelineTuiReducer).GetMethods(),
            method => method.Name.Contains("Command", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(PipelineTuiHost).GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => field.Name.Contains("input", StringComparison.OrdinalIgnoreCase));
    }

    private static string Render(
        PipelineTuiRenderer renderer,
        PipelineTuiState state,
        DateTimeOffset now)
    {
        var console = new TestConsole();
        console.Profile.Width = 160;
        console.Profile.Height = 50;
        console.Write(renderer.RenderAt(state, new(160, 50), new(true, false), now));
        return console.Output;
    }
}

public sealed class PlainAndHostTests
{
    [Fact]
    public void AutoRedirectedAndUnsupportedRichChoosePlain()
    {
        var redirected = new TerminalDetection(true, false, false, false, new(120, 30));
        Assert.Equal(UiMode.Plain, UiModeSelector.Select(UiMode.Auto, redirected).EffectiveMode);
        Assert.NotNull(UiModeSelector.Select(UiMode.Rich, redirected).Warning);
        Assert.Equal(UiMode.Plain, UiModeSelector.Select(UiMode.Plain,
            redirected with { OutputRedirected = false, Interactive = true }).EffectiveMode);
    }

    [Fact]
    public void PlainFormattingHasStableBoundedLinesAndNoAnsi()
    {
        string line = PlainEventFormatter.Format(
            AppEvent.TranscriptionCompleted("[red]" + new string('x', 1000)),
            new DateTimeOffset(2026, 7, 26, 12, 41, 3, 184, TimeSpan.Zero))!;
        Assert.StartsWith("12:41:03.184  SOURCE", line);
        Assert.Contains("transcription", line);
        Assert.False(line.Contains('\u001b'));
        Assert.InRange(line.Length, 1, 600);
    }

    [Fact]
    public async Task ConcurrentReportsStayBoundedAndNeverUseWriterOnCallingThread()
    {
        var writer = new TrackingWriter();
        var console = new TestConsole();
        await using var host = new PipelineTuiHost(
            PipelineTopologyBuilder.Build(Plans.Realtime()),
            UiMode.Plain,
            console,
            writer,
            new PlainTerminal());

        Parallel.For(0, 10_000, index =>
            host.Report(AppEvent.LogicalUtteranceUpdated(new(
                1, 1, index + 1, $"partial {index}",
                false, false, index + 1, index * 20,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))));

        Assert.InRange(host.Snapshot.RecentEvents.Count, 1, 100);
        Assert.Equal(0, writer.CallingThreadWrites);
    }

    [Fact]
    public async Task RichInvalidationsAreCoalescedAndRendererIsSingleOwner()
    {
        var renderer = new CountingRenderer();
        var terminal = new RichTerminal();
        var console = new TestConsole();
        await using (var host = new PipelineTuiHost(
            PipelineTopologyBuilder.Build(Plans.Realtime()),
            UiMode.Rich,
            console,
            new StringWriter(),
            terminal,
            renderer))
        {
            Parallel.For(0, 10_000, index =>
                host.Report(AppEvent.LogicalUtteranceUpdated(new(
                    1, 1, index + 1, $"partial {index}",
                    false, false, index + 1, index * 20,
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))));
            Assert.True(renderer.Rendered.Wait(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, renderer.MaximumConcurrent);
        Assert.InRange(renderer.Calls, 1, 100);
        Assert.True(terminal.RestoreCount > 0);
    }

    [Fact]
    public async Task RichRendererFailureFallsBackAndRestoresTerminal()
    {
        var terminal = new RichTerminal();
        var writer = new StringWriter();
        await using (var host = new PipelineTuiHost(
            PipelineTopologyBuilder.Build(Plans.Direct()),
            UiMode.Rich,
            new TestConsole(),
            writer,
            terminal,
            new ThrowingRenderer()))
        {
            Assert.True(SpinWait.SpinUntil(
                () => host.EffectiveMode == UiMode.Plain,
                TimeSpan.FromSeconds(2)));
        }

        Assert.Contains("continuing in plain mode", writer.ToString());
        Assert.True(terminal.RestoreCount > 0);
    }

    [Theory]
    [InlineData(new[] { "--ui", "plain" }, UiMode.Plain)]
    [InlineData(new[] { "run", "--ui", "rich" }, UiMode.Rich)]
    [InlineData(new[] { "--ui", "auto", "--dry-run" }, UiMode.Auto)]
    public void CliParsesUi(string[] args, UiMode expected)
    {
        CliParseResult result = FoxTransCli.Parse(args);
        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Options!.Ui);
    }

    private sealed class PlainTerminal : ITerminalEnvironment
    {
        public TerminalDetection Detect() => new(true, false, false, false, new(0, 0));
        public void Restore() { }
    }

    private sealed class RichTerminal : ITerminalEnvironment
    {
        public int RestoreCount;
        public TerminalDetection Detect() =>
            new(false, true, true, false, new(120, 30));
        public void Restore() => Interlocked.Increment(ref RestoreCount);
    }

    private sealed class CountingRenderer : IPipelineTuiRenderer
    {
        private int _active;
        public int Calls;
        public int MaximumConcurrent;
        public ManualResetEventSlim Rendered { get; } = new();
        public IRenderable Render(
            PipelineTuiState state,
            TerminalViewport viewport,
            TuiRenderCapabilities capabilities)
        {
            int active = Interlocked.Increment(ref _active);
            int current;
            do
            {
                current = Volatile.Read(ref MaximumConcurrent);
                if (current >= active)
                    break;
            } while (Interlocked.CompareExchange(
                ref MaximumConcurrent, active, current) != current);
            Interlocked.Increment(ref Calls);
            Rendered.Set();
            Interlocked.Decrement(ref _active);
            return new Text("frame");
        }
    }

    private sealed class ThrowingRenderer : IPipelineTuiRenderer
    {
        public IRenderable Render(
            PipelineTuiState state,
            TerminalViewport viewport,
            TuiRenderCapabilities capabilities) =>
            throw new IOException("controlled renderer failure");
    }

    private sealed class TrackingWriter : StringWriter
    {
        private readonly int _creator = Environment.CurrentManagedThreadId;
        public int CallingThreadWrites;
        public override void WriteLine(string? value)
        {
            if (Environment.CurrentManagedThreadId == _creator)
                CallingThreadWrites++;
            base.WriteLine(value);
        }
    }
}

internal static class Plans
{
    private static readonly ResolvedAudioInput Audio =
        new(2, "Test [microphone]", new(16000, 16, 1));
    private static IReadOnlyList<ResolvedOscEndpoint> Outputs(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new ResolvedOscEndpoint("127.0.0.1", 9000 + index, true))
            .ToArray();

    public static ResolvedExecutionPlan Direct(int outputs = 1)
    {
        var config = new FoxTransConfig(
            Audio: new("2"),
            Pipeline: new(
                new WebRtcVadConfig("natural-speech"),
                new OpenAiChatAudioConfig(
                    "https://user:pass@example.test/v1?secret=query",
                    "direct-secret", "audio-model", "translate this complete prompt")),
            Outputs: Enumerable.Range(0, outputs)
                .Select(index => (OutputProviderConfig)new VrChatOscConfig(
                    $"127.0.0.1:{9000 + index}", true)).ToArray());
        return new(
            config,
            PipelineKind.DirectAudioTranslation,
            Audio,
            Outputs(outputs),
            Vad: ConfigResolver.ResolveVad((WebRtcVadConfig)config.Pipeline!.Vad!),
            Direct: new(
                new("https://user:pass@example.test/v1/chat/completions?secret=query"),
                "direct-secret", "audio-model", "translate this complete prompt"));
    }

    public static ResolvedExecutionPlan Batch(int outputs = 1)
    {
        var config = new FoxTransConfig(
            Audio: new("2"),
            Pipeline: new(
                new WebRtcVadConfig("short-phrases"),
                new OpenAiTranscriptionConfig(),
                new OpenAiChatConfig()),
            Outputs: Enumerable.Range(0, outputs)
                .Select(index => (OutputProviderConfig)new VrChatOscConfig(
                    $"127.0.0.1:{9000 + index}", true)).ToArray());
        return new(
            config,
            PipelineKind.BatchTranscriptionTranslation,
            Audio,
            Outputs(outputs),
            Vad: ConfigResolver.ResolveVad((WebRtcVadConfig)config.Pipeline!.Vad!),
            Transcription: new(
                new("https://stt.example.test/v1/audio/transcriptions"),
                "stt-secret", "whisper", "ru", OpenAiTranscriptionRequestFormat.Json),
            Translation: new(
                new("https://llm.example.test/v1/chat/completions"),
                "translation-secret", "translator", "private prompt"));
    }

    public static ResolvedExecutionPlan Realtime(int outputs = 1)
    {
        var config = new FoxTransConfig(
            Audio: new("2"),
            Pipeline: new(
                Speech: new VoxtralFoxConfig(),
                Translation: new OpenAiChatConfig(),
                Realtime: new RealtimeConfig("responsive")),
            Outputs: Enumerable.Range(0, outputs)
                .Select(index => (OutputProviderConfig)new VrChatOscConfig(
                    $"127.0.0.1:{9000 + index}", true)).ToArray());
        return new(
            config,
            PipelineKind.RealtimeTranscriptionTranslation,
            Audio,
            Outputs(outputs),
            Translation: new(
                new("https://llm.example.test/v1/chat/completions"),
                "translation-secret", "translator", "private prompt"),
            Voxtral: new(
                new("http://voxtral.test/health"),
                new("ws://voxtral.test/v1/realtime/transcription"),
                "voxtral-secret", 480),
            Realtime: new(250, 700, 2, 2500, 800));
    }
}
