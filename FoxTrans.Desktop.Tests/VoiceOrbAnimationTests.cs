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

        Assert.True(loudState.CoreScale > quietState.CoreScale + 0.06f);
        Assert.True(loudState.CoreScale < quietState.CoreScale + 0.14f);
        Assert.True(loudState.Energy > quietState.Energy + 0.5f);
        Assert.True(loudState.HaloOpacity > quietState.HaloOpacity);
        Assert.True(loudState.SpeechActivity > 0);
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
    public void SpeechEnergyUsesASmoothVisualEnvelope()
    {
        var model = new VoiceOrbAnimationModel();
        VoiceOrbRenderState quiet = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.01f, 0.02f),
            Start,
            false);
        VoiceOrbRenderState firstLoud = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.5f, 0.9f),
            Start + TimeSpan.FromMilliseconds(16),
            false);
        VoiceOrbRenderState settled = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.5f, 0.9f),
            Start + TimeSpan.FromMilliseconds(500),
            false);

        Assert.True(firstLoud.Energy > quiet.Energy);
        Assert.True(firstLoud.Energy < 0.2f);
        Assert.True(settled.Energy > firstLoud.Energy + 0.5f);
    }

    [Fact]
    public void SpeechNoticeablyAcceleratesFluidPhasesWithoutJumping()
    {
        var quietModel = new VoiceOrbAnimationModel();
        var speechModel = new VoiceOrbAnimationModel();
        VoiceOrbRenderState quietStart = quietModel.Update(
            VoiceVisualizationMode.Listening,
            Frame(0.01f, 0.02f),
            Start,
            false);
        VoiceOrbRenderState speechStart = speechModel.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.01f, 0.02f),
            Start,
            false);

        VoiceOrbRenderState quiet = quietModel.Update(
            VoiceVisualizationMode.Listening,
            Frame(0.01f, 0.02f),
            Start + TimeSpan.FromMilliseconds(250),
            false);
        VoiceOrbRenderState speech = speechModel.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.5f, 0.9f, Enumerable.Repeat(0.6f, 12).ToArray()),
            Start + TimeSpan.FromMilliseconds(250),
            false);

        float quietTravel = quiet.MidPhase - quietStart.MidPhase;
        float speechTravel = speech.MidPhase - speechStart.MidPhase;
        Assert.True(speechTravel > quietTravel * 3);
        Assert.InRange(speechTravel, 0.5f, 1.8f);
    }

    [Fact]
    public void FluidSimulationAdvectsMassAndRespondsStronglyToSpeech()
    {
        var quietSimulation = new VoiceOrbFluidSimulation();
        var speechSimulation = new VoiceOrbFluidSimulation();
        var quietModel = new VoiceOrbAnimationModel();
        var speechModel = new VoiceOrbAnimationModel();
        float initialWater = speechSimulation.MeanWater;
        float initialMixing = speechSimulation.MeanMixing;
        byte[] initialTexture = speechSimulation.Update(
            speechModel.Update(
                VoiceVisualizationMode.Speech,
                Frame(0.01f, 0.02f),
                Start,
                false),
            0,
            false).ToArray();

        for (int index = 1; index <= 90; index++)
        {
            DateTimeOffset now =
                Start + TimeSpan.FromSeconds(index / 60d);
            VoiceOrbRenderState quiet = quietModel.Update(
                VoiceVisualizationMode.Listening,
                Frame(0.01f, 0.02f),
                now,
                false);
            VoiceOrbRenderState speech = speechModel.Update(
                VoiceVisualizationMode.Speech,
                Frame(
                    0.5f,
                    0.9f,
                    Enumerable.Repeat(0.6f, 12).ToArray()),
                now,
                false);
            quietSimulation.Update(
                quiet,
                index / 60d,
                false);
            speechSimulation.Update(
                speech,
                index / 60d,
                false);
        }

        byte[] movedTexture = speechSimulation.Update(
            speechModel.Update(
                VoiceVisualizationMode.Speech,
                Frame(
                    0.5f,
                    0.9f,
                    Enumerable.Repeat(0.6f, 12).ToArray()),
                Start + TimeSpan.FromSeconds(1.51),
                false),
            1.51,
            false).ToArray();
        int changedWaterCells = Enumerable.Range(
                0,
                VoiceOrbFluidSimulation.Resolution *
                VoiceOrbFluidSimulation.Resolution)
            .Count(index =>
                Math.Abs(
                    initialTexture[index * 4] -
                    movedTexture[index * 4]) > 8);

        Assert.InRange(
            Math.Abs(speechSimulation.MeanWater - initialWater),
            0,
            0.035f);
        Assert.True(
            speechSimulation.MeanSpeed >
            quietSimulation.MeanSpeed * 3.5f);
        Assert.True(speechSimulation.MeanMixing > initialMixing);
        Assert.True(
            speechSimulation.MeanMixing >
            quietSimulation.MeanMixing);
        Assert.True(
            changedWaterCells >
            VoiceOrbFluidSimulation.Resolution *
            VoiceOrbFluidSimulation.Resolution / 5);
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

    [Fact]
    public void LowMidAndHighBandsDriveIndependentIntegratedPhases()
    {
        VoiceOrbRenderState quiet = DrivenBandState(-1);
        VoiceOrbRenderState low = DrivenBandState(1);
        VoiceOrbRenderState mid = DrivenBandState(5);
        VoiceOrbRenderState high = DrivenBandState(10);

        Assert.True(low.LowPhase > quiet.LowPhase);
        Assert.True(mid.MidPhase > quiet.MidPhase);
        Assert.True(high.HighPhase > quiet.HighPhase);
        Assert.True(low.LowEnergy > low.MidEnergy);
        Assert.True(mid.MidEnergy > mid.HighEnergy);
        Assert.True(high.HighEnergy > high.LowEnergy);
    }

    [Fact]
    public void MissingFramesDecayEveryBandWithDistinctRelease()
    {
        float[] spectrum =
            Enumerable.Repeat(0.8f, Pcm16AudioFeatureExtractor.SpectrumBandCount)
                .ToArray();
        var model = new VoiceOrbAnimationModel();
        _ = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.2f, 0.3f, spectrum),
            Start,
            false);

        VoiceOrbRenderState decayed = model.Update(
            VoiceVisualizationMode.Speech,
            null,
            Start + TimeSpan.FromMilliseconds(100),
            false);

        Assert.InRange(decayed.LowEnergy, 0.55f, 0.60f);
        Assert.InRange(decayed.MidEnergy, 0.45f, 0.49f);
        Assert.InRange(decayed.HighEnergy, 0.36f, 0.40f);
        Assert.True(decayed.LowEnergy > decayed.MidEnergy);
        Assert.True(decayed.MidEnergy > decayed.HighEnergy);
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

        Assert.InRange(processing.ProcessingIntensity, 0.1f, 0.2f);
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

    private static VoiceOrbRenderState DrivenBandState(int band)
    {
        var model = new VoiceOrbAnimationModel();
        _ = model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.04f, 0.06f),
            Start,
            false);
        float[] spectrum =
            new float[Pcm16AudioFeatureExtractor.SpectrumBandCount];
        if (band >= 0)
            spectrum[band] = 0.9f;
        return model.Update(
            VoiceVisualizationMode.Speech,
            Frame(0.04f, 0.06f, spectrum),
            Start + TimeSpan.FromMilliseconds(100),
            false);
    }
}
