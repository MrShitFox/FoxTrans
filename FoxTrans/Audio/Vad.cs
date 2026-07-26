using System.Runtime.CompilerServices;
using WebRtcVadSharp;

public enum SegmentationUpdateKind
{
    SpeechStarted,
    SegmentCompleted,
    ShortPhraseIgnored
}

public sealed record SegmentationUpdate(
    SegmentationUpdateKind Kind,
    AudioSegment? Segment = null,
    TimeSpan? Duration = null);

public interface IAudioSegmenter
{
    IAsyncEnumerable<SegmentationUpdate> SegmentAsync(
        IAsyncEnumerable<AudioFrame> frames,
        CancellationToken cancellationToken);
}

public sealed record VadSegmentationSettings(
    int MinSpeechFrames,
    int MinSilenceFrames,
    int PreRollFrames,
    int MinPhraseLengthMs)
{
    public static VadSegmentationSettings FromConfig(AppConfig.VadConfig config) =>
        new(
            config.MinSpeechFrames,
            config.MinSilenceFrames,
            config.PreRollFrames,
            config.MinPhraseLengthMs);
}

public sealed record VadSegmentationState(
    int SpeechFrames,
    int SilenceFrames,
    bool IsSpeaking,
    IReadOnlyList<AudioFrame> PreRoll,
    IReadOnlyList<AudioFrame> Phrase)
{
    public static VadSegmentationState Initial { get; } =
        new(0, 0, false, Array.Empty<AudioFrame>(), Array.Empty<AudioFrame>());
}

public sealed record VadTransition(VadSegmentationState State, SegmentationUpdate? Update);

public static class VadStateMachine
{
    public static VadTransition Advance(
        VadSegmentationState state,
        AudioFrame frame,
        bool isSpeech,
        VadSegmentationSettings settings)
    {
        int speechFrames = isSpeech ? state.SpeechFrames + 1 : 0;
        int silenceFrames = isSpeech ? 0 : state.SilenceFrames + 1;

        if (!state.IsSpeaking)
        {
            AudioFrame[] preRoll = [.. state.PreRoll, frame];
            if (settings.PreRollFrames == 0)
            {
                preRoll = [];
            }
            else if (preRoll.Length > settings.PreRollFrames)
            {
                preRoll = preRoll[^settings.PreRollFrames..];
            }

            if (speechFrames < settings.MinSpeechFrames)
            {
                return new VadTransition(
                    new VadSegmentationState(speechFrames, silenceFrames, false, preRoll, state.Phrase),
                    null);
            }

            return new VadTransition(
                new VadSegmentationState(speechFrames, silenceFrames, true, [], preRoll),
                new SegmentationUpdate(SegmentationUpdateKind.SpeechStarted));
        }

        AudioFrame[] phrase = [.. state.Phrase, frame];
        if (silenceFrames < settings.MinSilenceFrames)
        {
            return new VadTransition(
                new VadSegmentationState(speechFrames, silenceFrames, true, state.PreRoll, phrase),
                null);
        }

        AudioSegment segment = CreateSegment(phrase);
        var reset = new VadSegmentationState(speechFrames, silenceFrames, false, [], []);
        SegmentationUpdate update = segment.Duration.TotalMilliseconds < settings.MinPhraseLengthMs
            ? new SegmentationUpdate(SegmentationUpdateKind.ShortPhraseIgnored, Duration: segment.Duration)
            : new SegmentationUpdate(SegmentationUpdateKind.SegmentCompleted, segment, segment.Duration);

        return new VadTransition(reset, update);
    }

    private static AudioSegment CreateSegment(IReadOnlyList<AudioFrame> frames)
    {
        if (frames.Count == 0)
        {
            throw new InvalidOperationException("A phrase cannot be created without audio frames.");
        }

        AudioFormat format = frames[0].Format;
        int length = frames.Sum(static frame => frame.Pcm.Length);
        byte[] pcm = new byte[length];
        int offset = 0;

        foreach (AudioFrame frame in frames)
        {
            if (frame.Format != format)
            {
                throw new InvalidOperationException("The audio format changed inside a phrase.");
            }

            frame.Pcm.Span.CopyTo(pcm.AsSpan(offset));
            offset += frame.Pcm.Length;
        }

        return new AudioSegment(pcm, format);
    }
}

public sealed class WebRtcVadSegmenter : IAudioSegmenter, IDisposable
{
    private readonly WebRtcVad _vad = new() { OperatingMode = OperatingMode.VeryAggressive };
    private readonly VadSegmentationSettings _settings;

    public WebRtcVadSegmenter(AppConfig.VadConfig config)
    {
        _settings = VadSegmentationSettings.FromConfig(config);
    }

    public async IAsyncEnumerable<SegmentationUpdate> SegmentAsync(
        IAsyncEnumerable<AudioFrame> frames,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        VadSegmentationState state = VadSegmentationState.Initial;

        await foreach (AudioFrame frame in frames.WithCancellation(cancellationToken))
        {
            ValidateFrame(frame);
            bool isSpeech = _vad.HasSpeech(
                frame.Pcm.ToArray(),
                SampleRate.Is16kHz,
                FrameLength.Is20ms);

            VadTransition transition = VadStateMachine.Advance(state, frame, isSpeech, _settings);
            state = transition.State;
            if (transition.Update is not null)
            {
                yield return transition.Update;
            }
        }
    }

    private static void ValidateFrame(AudioFrame frame)
    {
        var required = new AudioFormat(16000, 16, 1);
        if (frame.Format != required || frame.Pcm.Length != 640)
        {
            throw new InvalidDataException(
                "WebRTC VAD requires 20 ms frames of 16 kHz, 16-bit mono PCM audio.");
        }
    }

    public void Dispose() => _vad.Dispose();
}
