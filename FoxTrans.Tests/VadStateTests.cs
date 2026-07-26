using Xunit;

public sealed class VadStateTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public void SpeechStartsOnlyAfterConfiguredConsecutiveFrames()
    {
        var settings = new VadSegmentationSettings(3, 2, 4, 0);
        VadSegmentationState state = VadSegmentationState.Initial;

        VadTransition first = Advance(state, 1, true, settings);
        VadTransition interruption = Advance(first.State, 2, false, settings);
        VadTransition speech1 = Advance(interruption.State, 3, true, settings);
        VadTransition speech2 = Advance(speech1.State, 4, true, settings);
        VadTransition speech3 = Advance(speech2.State, 5, true, settings);

        Assert.Null(first.Update);
        Assert.Null(interruption.Update);
        Assert.Null(speech1.Update);
        Assert.Null(speech2.Update);
        Assert.Equal(SegmentationUpdateKind.SpeechStarted, speech3.Update?.Kind);
        Assert.True(speech3.State.IsSpeaking);
    }

    [Fact]
    public void CompletedSegmentIncludesBoundedPreRollAndTrailingSilence()
    {
        var settings = new VadSegmentationSettings(2, 1, 3, 0);
        VadSegmentationState state = VadSegmentationState.Initial;

        state = Advance(state, 1, false, settings).State;
        state = Advance(state, 2, false, settings).State;
        state = Advance(state, 3, true, settings).State;
        VadTransition started = Advance(state, 4, true, settings);
        VadTransition completed = Advance(started.State, 5, false, settings);

        Assert.Equal(SegmentationUpdateKind.SegmentCompleted, completed.Update?.Kind);
        byte[] pcm = completed.Update!.Segment!.Pcm.ToArray();
        Assert.Equal([2, 3, 4, 5], FrameMarkers(pcm));
    }

    [Fact]
    public void PhraseEndsOnlyAfterConfiguredConsecutiveSilence()
    {
        var settings = new VadSegmentationSettings(1, 3, 1, 0);
        VadTransition started = Advance(VadSegmentationState.Initial, 1, true, settings);
        VadTransition silence1 = Advance(started.State, 2, false, settings);
        VadTransition speech = Advance(silence1.State, 3, true, settings);
        VadTransition silence2 = Advance(speech.State, 4, false, settings);
        VadTransition silence3 = Advance(silence2.State, 5, false, settings);
        VadTransition completed = Advance(silence3.State, 6, false, settings);

        Assert.Null(silence1.Update);
        Assert.Null(speech.Update);
        Assert.Null(silence2.Update);
        Assert.Null(silence3.Update);
        Assert.Equal(SegmentationUpdateKind.SegmentCompleted, completed.Update?.Kind);
    }

    [Fact]
    public void ShortPhraseIsIgnored()
    {
        var settings = new VadSegmentationSettings(1, 1, 1, 100);
        VadTransition started = Advance(VadSegmentationState.Initial, 1, true, settings);
        VadTransition completed = Advance(started.State, 2, false, settings);

        Assert.Equal(SegmentationUpdateKind.ShortPhraseIgnored, completed.Update?.Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(40), completed.Update?.Duration);
        Assert.Null(completed.Update?.Segment);
    }

    [Fact]
    public void CompletionResetsPhraseStateAndCountersRecover()
    {
        var settings = new VadSegmentationSettings(1, 1, 2, 0);
        VadTransition started = Advance(VadSegmentationState.Initial, 1, true, settings);
        VadTransition completed = Advance(started.State, 2, false, settings);

        Assert.False(completed.State.IsSpeaking);
        Assert.Empty(completed.State.Phrase);
        Assert.Empty(completed.State.PreRoll);

        VadTransition next = Advance(completed.State, 3, true, settings);
        Assert.Equal(SegmentationUpdateKind.SpeechStarted, next.Update?.Kind);
        Assert.Single(next.State.Phrase);
    }

    [Fact]
    public void MultipleSequentialPhrasesRemainIndependent()
    {
        var settings = new VadSegmentationSettings(1, 1, 1, 0);
        VadSegmentationState state = VadSegmentationState.Initial;
        var segments = new List<AudioSegment>();

        foreach ((int marker, bool speech) in new[]
                 {
                     (1, true), (2, false), (3, true), (4, false)
                 })
        {
            VadTransition transition = Advance(state, marker, speech, settings);
            state = transition.State;
            if (transition.Update?.Segment is not null)
            {
                segments.Add(transition.Update.Segment);
            }
        }

        Assert.Equal(2, segments.Count);
        Assert.Equal([1, 2], FrameMarkers(segments[0].Pcm.ToArray()));
        Assert.Equal([3, 4], FrameMarkers(segments[1].Pcm.ToArray()));
    }

    private static VadTransition Advance(
        VadSegmentationState state,
        int marker,
        bool speech,
        VadSegmentationSettings settings) =>
        VadStateMachine.Advance(state, Frame(marker), speech, settings);

    private static AudioFrame Frame(int marker)
    {
        byte[] pcm = new byte[640];
        pcm[0] = (byte)marker;
        return new AudioFrame(pcm, Format);
    }

    private static int[] FrameMarkers(byte[] pcm) =>
        Enumerable.Range(0, pcm.Length / 640).Select(index => (int)pcm[index * 640]).ToArray();
}
