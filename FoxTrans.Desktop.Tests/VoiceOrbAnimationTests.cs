using FoxTrans.Desktop.Models;
using Xunit;

public sealed class VoiceOrbAnimationTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void IdleIsStableAtFakeTime()
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
        Assert.Equal(first.Contour.ToArray(), second.Contour.ToArray());
    }

    [Fact]
    public void AmplitudeInfluencesCoreSize()
    {
        var quiet = new VoiceOrbAnimationModel();
        var loud = new VoiceOrbAnimationModel();

        float quietScale = quiet.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.01f, 0.02f),
            Start,
            false).CoreScale;
        float loudScale = loud.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.5f, 0.9f),
            Start,
            false).CoreScale;

        Assert.True(loudScale > quietScale + 0.1f);
    }

    [Fact]
    public void SpectralBandsInfluenceAsymmetricContour()
    {
        var model = new VoiceOrbAnimationModel();
        float[] spectrum = new float[Pcm16AudioFeatureExtractor.SpectrumBandCount];
        spectrum[3] = 1;

        VoiceOrbRenderState state = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.2f, 0.3f, spectrum),
            Start,
            reducedMotion: true);

        Assert.True(state.Contour.Span[8] > state.Contour.Span[0]);
    }

    [Theory]
    [InlineData(VoiceVisualizationMode.Idle)]
    [InlineData(VoiceVisualizationMode.Listening)]
    [InlineData(VoiceVisualizationMode.Speech)]
    [InlineData(VoiceVisualizationMode.Processing)]
    [InlineData(VoiceVisualizationMode.Success)]
    [InlineData(VoiceVisualizationMode.Error)]
    [InlineData(VoiceVisualizationMode.Stopping)]
    public void EveryExplicitModeProducesVisibleFiniteState(
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
        Assert.Equal(VoiceOrbAnimationModel.ContourPointCount, state.Contour.Length);
    }

    [Fact]
    public void ErrorStateContractsAndRemainsVisiblyDifferent()
    {
        var normal = new VoiceOrbAnimationModel();
        var error = new VoiceOrbAnimationModel();

        VoiceOrbRenderState listening = normal.Update(
            VoiceVisualizationMode.Listening,
            Frame(0, 0),
            Start,
            true);
        VoiceOrbRenderState failed = error.Update(
            VoiceVisualizationMode.Error,
            Frame(0, 0),
            Start,
            true);

        Assert.True(failed.CoreScale < listening.CoreScale);
        Assert.True(failed.HaloOpacity > listening.HaloOpacity);
    }

    [Fact]
    public void ReducedMotionRemovesTimeBasedDeformation()
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

        Assert.Equal(0, first.Rotation);
        Assert.Equal(0, second.Rotation);
        Assert.Equal(first.Contour.ToArray(), second.Contour.ToArray());
    }

    [Fact]
    public void ModelRetainsNoRawAudio()
    {
        var model = new VoiceOrbAnimationModel();
        _ = model.Update(
            VoiceVisualizationMode.Listening,
            Frame(0.1f, 0.2f),
            Start,
            false);

        Assert.False(model.RetainsRawAudio);
        Assert.Equal(
            Pcm16AudioFeatureExtractor.SpectrumBandCount,
            model.RetainedBandCount);
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
