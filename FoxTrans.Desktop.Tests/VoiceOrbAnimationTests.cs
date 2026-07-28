using FoxTrans.Desktop.Models;
using Xunit;

public sealed class VoiceOrbAnimationTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void IdenticalInputsProduceStableUniformStateAtFakeTime()
    {
        var left = new VoiceOrbAnimationModel();
        var right = new VoiceOrbAnimationModel();

        VoiceOrbRenderState first = left.Update(
            VoiceVisualizationMode.Idle,
            Frame(0, 0),
            Start,
            false);
        VoiceOrbRenderState second = right.Update(
            VoiceVisualizationMode.Idle,
            Frame(0, 0),
            Start,
            false);

        Assert.Equal(first.CoreScale, second.CoreScale);
        Assert.Equal(
            first.SpectralBands.ToArray(),
            second.SpectralBands.ToArray());
    }

    [Fact]
    public void RmsAndPeakProduceMeaningfullyDifferentSpeechPressure()
    {
        var quiet = new VoiceOrbAnimationModel();
        var loud = new VoiceOrbAnimationModel();

        VoiceOrbRenderState quietState = quiet.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.01f, 0.02f),
            Start,
            false);
        VoiceOrbRenderState loudState = loud.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.5f, 0.9f),
            Start,
            false);

        Assert.True(loudState.CoreScale > quietState.CoreScale + 0.1f);
        Assert.True(loudState.Energy > quietState.Energy + 0.5f);
        Assert.True(loudState.HaloOpacity > quietState.HaloOpacity);
    }

    [Fact]
    public void PeakSpikeCreatesASeparateShortImpulseUniform()
    {
        var model = new VoiceOrbAnimationModel();
        _ = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.04f, 0.05f),
            Start,
            false);

        VoiceOrbRenderState spike = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.08f, 0.95f),
            Start + TimeSpan.FromMilliseconds(16),
            false);
        VoiceOrbRenderState decayed = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.04f, 0.05f),
            Start + TimeSpan.FromMilliseconds(300),
            false);

        Assert.True(spike.PeakImpulse > 0.1f);
        Assert.True(decayed.PeakImpulse < spike.PeakImpulse);
    }

    [Fact]
    public void LowMidAndHighBandsRemainSeparated()
    {
        float[] spectrum = new float[Pcm16AudioFeatureExtractor.SpectrumBandCount];
        spectrum[0] = 0.8f;
        spectrum[1] = 0.6f;
        spectrum[5] = 0.35f;
        spectrum[10] = 0.12f;
        var model = new VoiceOrbAnimationModel();

        VoiceOrbRenderState state = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.2f, 0.3f, spectrum),
            Start,
            false);

        Assert.True(state.LowEnergy > state.MidEnergy);
        Assert.True(state.MidEnergy > state.HighEnergy);
        Assert.Equal(spectrum, state.SpectralBands.ToArray());
    }

    [Theory]
    [InlineData(VoiceVisualizationMode.Idle)]
    [InlineData(VoiceVisualizationMode.Listening)]
    [InlineData(VoiceVisualizationMode.Speech)]
    [InlineData(VoiceVisualizationMode.Processing)]
    [InlineData(VoiceVisualizationMode.Success)]
    [InlineData(VoiceVisualizationMode.Error)]
    [InlineData(VoiceVisualizationMode.Stopping)]
    public void EveryExplicitModeProducesFiniteShaderUniforms(
        VoiceVisualizationMode mode)
    {
        var model = new VoiceOrbAnimationModel();
        VoiceOrbRenderState state = model.Update(
            mode,
            Frame(0.1f, 0.2f),
            Start,
            false);

        Assert.True(float.IsFinite(state.CoreScale));
        Assert.InRange(state.CoreScale, 0.75f, 1.5f);
        Assert.InRange(state.HaloOpacity, 0.05f, 0.8f);
        Assert.Equal((float)mode, state.StateValue);
        Assert.Equal(
            Pcm16AudioFeatureExtractor.SpectrumBandCount,
            state.SpectralBands.Length);
    }

    [Fact]
    public void ProcessingAndErrorHaveDedicatedUniforms()
    {
        var processingModel = new VoiceOrbAnimationModel();
        var errorModel = new VoiceOrbAnimationModel();
        var listeningModel = new VoiceOrbAnimationModel();

        VoiceOrbRenderState processing = processingModel.Update(
            VoiceVisualizationMode.Processing,
            Frame(0.1f, 0.2f),
            Start,
            false);
        VoiceOrbRenderState error = errorModel.Update(
            VoiceVisualizationMode.Error,
            Frame(0, 0),
            Start,
            true);
        VoiceOrbRenderState listening = listeningModel.Update(
            VoiceVisualizationMode.Listening,
            Frame(0, 0),
            Start,
            true);

        Assert.Equal(1, processing.ProcessingIntensity);
        Assert.Equal(0, listening.ProcessingIntensity);
        Assert.True(error.ErrorPulse > 0.5f);
        Assert.True(error.CoreScale < listening.CoreScale);
    }

    [Fact]
    public void SuccessPulseBloomsAndDecaysWithinBoundedTime()
    {
        var model = new VoiceOrbAnimationModel();
        _ = model.Update(
            VoiceVisualizationMode.Success,
            Frame(0, 0),
            Start,
            false);
        VoiceOrbRenderState bloom = model.Update(
            VoiceVisualizationMode.Success,
            Frame(0, 0),
            Start + TimeSpan.FromMilliseconds(220),
            false);
        VoiceOrbRenderState settled = model.Update(
            VoiceVisualizationMode.Success,
            Frame(0, 0),
            Start + TimeSpan.FromMilliseconds(800),
            false);

        Assert.True(bloom.SuccessPulse > 0.4f);
        Assert.Equal(0, settled.SuccessPulse);
    }

    [Fact]
    public void ReducedMotionDisablesContinuousShaderDeformation()
    {
        var firstModel = new VoiceOrbAnimationModel();
        var secondModel = new VoiceOrbAnimationModel();
        AudioVisualFrame frame = Frame(0.2f, 0.3f);

        VoiceOrbRenderState first = firstModel.Update(
            VoiceVisualizationMode.Processing,
            frame,
            Start,
            true);
        VoiceOrbRenderState second = secondModel.Update(
            VoiceVisualizationMode.Processing,
            frame,
            Start + TimeSpan.FromSeconds(30),
            true);

        Assert.Equal(0, first.ReducedMotionFactor);
        Assert.Equal(0, second.ReducedMotionFactor);
        Assert.Equal(first.CoreScale, second.CoreScale);
    }

    [Fact]
    public void ModelRetainsOnlyLatestBandsAndNoPcm()
    {
        var model = new VoiceOrbAnimationModel();
        float[] first = Enumerable.Repeat(0.1f, 12).ToArray();
        float[] latest = Enumerable.Repeat(0.7f, 12).ToArray();
        _ = model.Update(
            VoiceVisualizationMode.Listening,
            Frame(0.1f, 0.2f, first),
            Start,
            false);
        VoiceOrbRenderState state = model.Update(
            VoiceVisualizationMode.Listening,
            Frame(0.1f, 0.2f, latest),
            Start + TimeSpan.FromSeconds(1),
            false);

        Assert.False(model.RetainsRawAudio);
        Assert.Equal(12, model.RetainedBandCount);
        Assert.All(state.SpectralBands.ToArray(), value =>
            Assert.InRange(value, 0.65f, 0.7f));
    }

    private static AudioVisualFrame Frame(
        float rms,
        float peak,
        float[]? spectrum = null) =>
        new(
            rms,
            peak,
            peak >= 0.999f,
            rms > 0.02f,
            spectrum ?? new float[Pcm16AudioFeatureExtractor.SpectrumBandCount],
            1,
            Start);
}
