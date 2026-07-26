using Xunit;

public sealed class Session6CliAndDeviceTests
{
    public static IEnumerable<object[]> ValidCliCases()
    {
        yield return [Array.Empty<string>(), FoxTransCommand.Run, null!, false];
        yield return [new[] { "run" }, FoxTransCommand.Run, null!, false];
        yield return [new[] { "check" }, FoxTransCommand.Check, null!, false];
        yield return [new[] { "devices" }, FoxTransCommand.Devices, null!, false];
        yield return [new[] { "--help" }, FoxTransCommand.Help, null!, false];
        yield return [new[] { "-h" }, FoxTransCommand.Help, null!, false];
        yield return [new[] { "run", "--config", "relative.jsonc" }, FoxTransCommand.Run, "relative.jsonc", false];
        yield return [new[] { "--config", @"C:\config\foxtrans.jsonc", "check" }, FoxTransCommand.Check, @"C:\config\foxtrans.jsonc", false];
        yield return [new[] { "run", "--dry-run" }, FoxTransCommand.Run, null!, true];
    }

    [Theory]
    [MemberData(nameof(ValidCliCases))]
    public void ParsesSupportedCommands(
        string[] args,
        FoxTransCommand command,
        string? path,
        bool dryRun)
    {
        CliParseResult result = FoxTransCli.Parse(args);
        Assert.True(result.IsSuccess);
        Assert.Equal(command, result.Options!.Command);
        Assert.Equal(path, result.Options.ConfigPath);
        Assert.Equal(dryRun, result.Options.DryRun);
    }

    [Theory]
    [InlineData("wat")]
    [InlineData("--wat")]
    [InlineData("--config")]
    [InlineData("devices", "--config", "x")]
    [InlineData("check", "--dry-run")]
    [InlineData("run", "--dry-run", "--dry-run")]
    [InlineData("run", "--config", "a", "--config", "b")]
    [InlineData("--help", "run")]
    public void RejectsInvalidArguments(params string[] args)
    {
        CliParseResult result = FoxTransCli.Parse(args);
        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Error!);
    }

    [Fact]
    public void ExitCodesHaveStableMeanings()
    {
        Assert.Equal(0, FoxTransExitCodes.Success);
        Assert.Equal(1, FoxTransExitCodes.RuntimeFailure);
        Assert.Equal(2, FoxTransExitCodes.UsageOrConfiguration);
        Assert.Equal(3, FoxTransExitCodes.DependencyUnavailable);
    }

    private static readonly AudioFormat Format = new(16000, 16, 1);
    private static readonly AudioInputDevice[] Devices =
    [
        new(0, "Microphone (USB Audio Device)"),
        new(1, "Headset Microphone (Bluetooth)"),
        new(2, "Microphone Array")
    ];

    [Theory]
    [InlineData("default", 0)]
    [InlineData("1", 1)]
    [InlineData("headset microphone (bluetooth)", 1)]
    [InlineData("Headset", 1)]
    public void ResolvesAudioSelection(string selection, int expected)
    {
        ResolvedAudioInput result = AudioDeviceSelection.Resolve(selection, Devices, Format);
        Assert.Equal(expected, result.DeviceNumber);
        Assert.Equal(Format, result.Format);
    }

    [Theory]
    [InlineData("Microphone")]
    [InlineData("99")]
    [InlineData("missing")]
    public void RejectsAmbiguousUnknownAndInvalidAudioSelection(string selection)
    {
        Assert.Throws<AudioDeviceSelectionException>(
            () => AudioDeviceSelection.Resolve(selection, Devices, Format));
    }

    [Fact]
    public void RejectsEmptyAudioCatalogue()
    {
        AudioDeviceSelectionException error = Assert.Throws<AudioDeviceSelectionException>(
            () => AudioDeviceSelection.Resolve("default", [], Format));
        Assert.Contains("No microphone", error.Message);
    }

    [Fact]
    public void DeviceListingIsPureAndReadable()
    {
        string text = AudioDeviceSelection.FormatList(Devices);
        Assert.Contains("0  Microphone", text);
        Assert.Contains("\"device\": \"0\"", text);
    }

    [Fact]
    public void ResolvedDeviceNumberReachesNAudioBoundaryWithoutStartingCapture()
    {
        using var source = new NAudioMicrophoneSource(
            new ResolvedAudioInput(7, "Injected", Format));
        Assert.Equal(7, source.DeviceNumber);
    }

    [Fact]
    public void ExplicitConfigUsesExactPathAndNeverCreatesMissingConfig()
    {
        string directory = Path.Combine(Path.GetTempPath(), "foxtrans-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "custom.jsonc");
            File.WriteAllText(path, AppConfig.Serialize(AppConfig.Default()));
            ConfigLoadResult loaded = AppConfig.LoadExplicit(path);
            Assert.Equal(Path.GetFullPath(path), loaded.Path);
            Assert.Equal(ConfigLoadState.Loaded, loaded.State);
            Assert.Equal(
                Path.Combine(directory, "foxtrans.schema.json"),
                AppConfig.ResolveSchemaPath(loaded.Config!, loaded.Path));
            Assert.Throws<ConfigurationException>(
                () => AppConfig.LoadExplicit(Path.Combine(directory, "missing.jsonc")));
            Assert.False(File.Exists(Path.Combine(directory, "missing.jsonc")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
