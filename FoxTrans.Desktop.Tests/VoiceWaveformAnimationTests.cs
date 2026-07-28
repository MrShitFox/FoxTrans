using FoxTrans.Desktop.Models;
using Xunit;

public sealed class VoiceWaveformAnimationTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void IdenticalInputsProduceStableStateAtFakeTime()
    {
        var left = new VoiceWaveformAnimationModel();
        var right = new VoiceWaveformAnimationModel();

        VoiceWaveformRenderState first = left.Update(
            VoiceVisualizationMode.Idle,
            Frame(0, 0),
            Start,
            false);
        VoiceWaveformRenderState second = right.Update(
            VoiceVisualizationMode.Idle,
            Frame(0, 0),
            Start,
            false);

        Assert.Equal(first.Opacity, second.Opacity);
        Assert.Equal(first.BorderIntensity, second.BorderIntensity);
        Assert.Equal(first.SweepProgress, second.SweepProgress);
        Assert.Equal(first.BarHeights.ToArray(), second.BarHeights.ToArray());
    }

    [Fact]
    public void DifferentMicrophoneLevelsConvergeToTheSameVisualPresence()
    {
        var quiet = new VoiceWaveformAnimationModel();
        var loud = new VoiceWaveformAnimationModel();
        float[] quietSpectrum =
            [0.020f, 0.025f, 0.030f, 0.034f, 0.038f, 0.040f,
             0.038f, 0.034f, 0.030f, 0.026f, 0.022f, 0.018f];
        float[] loudSpectrum = quietSpectrum
            .Select(value => value * 12)
            .ToArray();

        VoiceWaveformRenderState quietState = Settle(
            quiet,
            Frame(0.018f, 0.070f, quietSpectrum),
            30);
        VoiceWaveformRenderState loudState = Settle(
            loud,
            Frame(0.32f, 0.85f, loudSpectrum),
            30);

        float quietAverage = Average(quietState.BarHeights.Span);
        float loudAverage = Average(loudState.BarHeights.Span);
        Assert.InRange(quietAverage, 0.62f, 0.92f);
        Assert.InRange(loudAverage, 0.62f, 0.92f);
        Assert.InRange(Math.Abs(quietAverage - loudAverage), 0, 0.08f);
        Assert.True(
            quietState.AdaptiveGain >
            loudState.AdaptiveGain * 4);
    }

    [Fact]
    public void SensitivityRecalibratesWhenMicrophoneGainChanges()
    {
        var model = new VoiceWaveformAnimationModel();
        float[] shape =
            [0.30f, 0.36f, 0.42f, 0.48f, 0.52f, 0.55f,
             0.52f, 0.48f, 0.42f, 0.36f, 0.30f, 0.24f];
        VoiceWaveformRenderState loud = Settle(
            model,
            Frame(0.32f, 0.85f, shape),
            12);
        float loudGain = loud.AdaptiveGain;

        VoiceWaveformRenderState quiet = default;
        float[] quietShape = shape.Select(value => value * 0.06f).ToArray();
        for (int index = 13; index <= 30; index++)
        {
            quiet = model.Update(
                VoiceVisualizationMode.Speech,
                Frame(0.018f, 0.070f, quietShape),
                Start + TimeSpan.FromMilliseconds(index * 33),
                false);
        }

        Assert.True(quiet.AdaptiveGain > loudGain * 3);
        Assert.True(quiet.VisualEnergy > 0.72f);
        Assert.True(Average(quiet.BarHeights.Span) > 0.55f);
    }

    [Fact]
    public void StableBackgroundNoiseIsNotPromotedToSpeech()
    {
        var model = new VoiceWaveformAnimationModel();
        AudioVisualFrame noise = Frame(
            0.012f,
            0.020f,
            Enumerable.Repeat(0.010f, 12).ToArray());

        VoiceWaveformRenderState state = Settle(
            model,
            noise,
            60,
            VoiceVisualizationMode.Listening);

        Assert.InRange(state.NoiseFloor, 0.010f, 0.014f);
        Assert.InRange(state.VisualEnergy, 0, 0.05f);
        Assert.True(Average(state.BarHeights.Span) < 0.14f);
    }

    [Fact]
    public void IndividualSpectrumBandsRemainIndependent()
    {
        float[] spectrum = new float[VoiceWaveformAnimationModel.BarCount];
        spectrum[8] = 0.9f;
        var model = new VoiceWaveformAnimationModel();

        VoiceWaveformRenderState state = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.03f, 0.05f, spectrum),
            Start,
            false);

        Assert.True(state.BarHeights.Span[8] > state.BarHeights.Span[7] + 0.22f);
        Assert.True(state.BarHeights.Span[8] > state.BarHeights.Span[9] + 0.30f);
    }

    [Fact]
    public void BarsUseFastAttackAndSlowerRelease()
    {
        var model = new VoiceWaveformAnimationModel();
        VoiceWaveformRenderState quiet = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.01f, 0.02f),
            Start,
            false);
        float quietAverage = Average(quiet.BarHeights.Span);
        VoiceWaveformRenderState attacking = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(
                0.5f,
                0.9f,
                Enumerable.Repeat(0.6f, 12).ToArray()),
            Start + TimeSpan.FromMilliseconds(16),
            false);
        float attackAverage = Average(attacking.BarHeights.Span);
        VoiceWaveformRenderState settled = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(
                0.5f,
                0.9f,
                Enumerable.Repeat(0.6f, 12).ToArray()),
            Start + TimeSpan.FromMilliseconds(500),
            false);
        float settledAverage = Average(settled.BarHeights.Span);
        VoiceWaveformRenderState releasing = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.01f, 0.02f),
            Start + TimeSpan.FromMilliseconds(600),
            false);
        float releaseAverage = Average(releasing.BarHeights.Span);

        Assert.True(attackAverage > quietAverage);
        Assert.True(attackAverage < settledAverage);
        Assert.True(releaseAverage > quietAverage + 0.20f);
        Assert.Equal(0.045, VoiceWaveformAnimationModel.AttackTimeSeconds, 3);
        Assert.Equal(0.160, VoiceWaveformAnimationModel.ReleaseTimeSeconds, 3);
    }

    [Theory]
    [InlineData(VoiceVisualizationMode.Idle)]
    [InlineData(VoiceVisualizationMode.Listening)]
    [InlineData(VoiceVisualizationMode.Speech)]
    [InlineData(VoiceVisualizationMode.Processing)]
    [InlineData(VoiceVisualizationMode.Success)]
    [InlineData(VoiceVisualizationMode.Error)]
    [InlineData(VoiceVisualizationMode.Stopping)]
    public void EveryModeProducesBoundedState(VoiceVisualizationMode mode)
    {
        var model = new VoiceWaveformAnimationModel();
        VoiceWaveformRenderState state = model.Update(
            mode,
            Frame(0.1f, 0.2f),
            Start,
            false);

        Assert.Equal((float)mode, state.StateValue);
        Assert.InRange(state.Opacity, 0, 1);
        Assert.InRange(state.BorderIntensity, 0, 1);
        Assert.InRange(state.VisualEnergy, 0, 1);
        Assert.InRange(state.AdaptiveGain, 0.18f, 12);
        Assert.InRange(state.NoiseFloor, 0.0015f, 0.080f);
        Assert.Equal(VoiceWaveformAnimationModel.BarCount, state.BarHeights.Length);
        Assert.All(
            state.BarHeights.ToArray(),
            value =>
            {
                Assert.True(float.IsFinite(value));
                Assert.InRange(value, 0, 1);
            });
    }

    [Fact]
    public void ProcessingSuccessAndErrorEffectsAreBounded()
    {
        var processing = new VoiceWaveformAnimationModel();
        _ = processing.Update(
            VoiceVisualizationMode.Processing,
            Frame(0, 0),
            Start,
            false);
        VoiceWaveformRenderState sweep = processing.Update(
            VoiceVisualizationMode.Processing,
            Frame(0, 0),
            Start + TimeSpan.FromMilliseconds(600),
            false);

        var success = new VoiceWaveformAnimationModel();
        _ = success.Update(
            VoiceVisualizationMode.Success,
            Frame(0, 0),
            Start,
            false);
        VoiceWaveformRenderState successPeak = success.Update(
            VoiceVisualizationMode.Success,
            Frame(0, 0),
            Start + TimeSpan.FromMilliseconds(225),
            false);
        VoiceWaveformRenderState successSettled = success.Update(
            VoiceVisualizationMode.Success,
            Frame(0, 0),
            Start + TimeSpan.FromMilliseconds(500),
            false);

        var error = new VoiceWaveformAnimationModel();
        _ = error.Update(
            VoiceVisualizationMode.Error,
            Frame(0, 0),
            Start,
            false);
        VoiceWaveformRenderState errorPeak = error.Update(
            VoiceVisualizationMode.Error,
            Frame(0, 0),
            Start + TimeSpan.FromMilliseconds(90),
            false);
        VoiceWaveformRenderState errorSettled = error.Update(
            VoiceVisualizationMode.Error,
            Frame(0, 0),
            Start + TimeSpan.FromMilliseconds(220),
            false);

        Assert.InRange(sweep.SweepProgress, 0.49f, 0.51f);
        Assert.True(successPeak.SuccessPulse > 0.95f);
        Assert.Equal(0, successSettled.SuccessPulse);
        Assert.True(errorPeak.ErrorPulse > 0.95f);
        Assert.Equal(0, errorSettled.ErrorPulse);
    }

    [Fact]
    public void ReducedMotionDisablesSweepAndPulses()
    {
        foreach (VoiceVisualizationMode mode in new[]
                 {
                     VoiceVisualizationMode.Processing,
                     VoiceVisualizationMode.Success,
                     VoiceVisualizationMode.Error
                 })
        {
            var model = new VoiceWaveformAnimationModel();
            VoiceWaveformRenderState first = model.Update(
                mode,
                Frame(0.2f, 0.3f),
                Start,
                true);
            VoiceWaveformRenderState second = model.Update(
                mode,
                Frame(0.2f, 0.3f),
                Start + TimeSpan.FromSeconds(30),
                true);

            Assert.Equal(-1, first.SweepProgress);
            Assert.Equal(0, first.SuccessPulse);
            Assert.Equal(0, first.ErrorPulse);
            Assert.Equal(
                first.BarHeights.ToArray(),
                second.BarHeights.ToArray());
        }
    }

    [Fact]
    public void StoppingFadesWithinQuarterSecond()
    {
        var model = new VoiceWaveformAnimationModel();
        VoiceWaveformRenderState start = model.Update(
            VoiceVisualizationMode.Stopping,
            Frame(0.2f, 0.3f),
            Start,
            false);
        float startAverage = Average(start.BarHeights.Span);
        VoiceWaveformRenderState faded = model.Update(
            VoiceVisualizationMode.Stopping,
            Frame(0.2f, 0.3f),
            Start + TimeSpan.FromMilliseconds(250),
            false);

        Assert.Equal(1, start.Opacity);
        Assert.InRange(faded.Opacity, 0.35f, 0.37f);
        Assert.True(
            Average(faded.BarHeights.Span) <
            startAverage);
    }

    [Fact]
    public void ModelRetainsOnlyFixedBarsAndNoPcm()
    {
        var model = new VoiceWaveformAnimationModel();
        _ = model.Update(
            VoiceVisualizationMode.Listening,
            Frame(0.1f, 0.2f),
            Start,
            false);

        Assert.False(model.RetainsRawAudio);
        Assert.Equal(Pcm16AudioFeatureExtractor.SpectrumBandCount, model.RetainedBandCount);
    }

    private static AudioVisualFrame Frame(
        float rms,
        float peak,
        float[]? spectrum = null) =>
        new(
            rms,
            peak,
            peak >= 0.999f,
            rms >= 0.015f || peak >= 0.060f,
            spectrum ?? new float[Pcm16AudioFeatureExtractor.SpectrumBandCount],
            1,
            Start);

    private static VoiceWaveformRenderState Settle(
        VoiceWaveformAnimationModel model,
        AudioVisualFrame frame,
        int frames,
        VoiceVisualizationMode mode = VoiceVisualizationMode.Speech)
    {
        VoiceWaveformRenderState state = default;
        for (int index = 0; index < frames; index++)
        {
            state = model.Update(
                mode,
                frame,
                Start + TimeSpan.FromMilliseconds(index * 33),
                false);
        }
        return state;
    }

    private static float Average(ReadOnlySpan<float> values)
    {
        float total = 0;
        foreach (float value in values)
            total += value;
        return values.IsEmpty ? 0 : total / values.Length;
    }
}
