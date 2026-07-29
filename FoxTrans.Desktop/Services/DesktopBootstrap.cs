namespace FoxTrans.Desktop.Services;

public sealed record DesktopBootstrapResult(
    string ConfigPath,
    FoxTransConfig? Config,
    ResolvedExecutionPlan? Plan,
    IReadOnlyList<ConfigIssue> Issues,
    IReadOnlyList<string> Warnings,
    ConfigLoadState? LoadState = null,
    IReadOnlyList<AudioInputDevice>? AudioInputs = null)
{
    public bool IsValid => Plan is not null && Issues.Count == 0;
    public bool IsPending =>
        Config is null &&
        Plan is null &&
        Issues.Count == 0 &&
        LoadState is null;
}

public static class DesktopBootstrap
{
    public static DesktopBootstrapResult Pending(string workingDirectory) =>
        new(
            Path.GetFullPath(Path.Combine(workingDirectory, "config.jsonc")),
            null,
            null,
            [],
            [],
            AudioInputs: []);

    public static DesktopBootstrapResult Load(
        string workingDirectory,
        IAudioInputDeviceCatalogue? devices = null,
        Func<string, string?>? environment = null)
    {
        string configPath = Path.GetFullPath(
            Path.Combine(workingDirectory, "config.jsonc"));
        try
        {
            ConfigLoadResult loaded = AppConfig.LoadOrCreate(workingDirectory);
            var warnings = new List<string>(loaded.Warnings);
            IReadOnlyList<AudioInputDevice> inputs =
                (devices ?? new NAudioInputDeviceCatalogue()).GetInputs();
            ExecutionPlanResolution resolution = ExecutionPlanResolver.Resolve(
                loaded.Config!,
                inputs,
                environment ?? Environment.GetEnvironmentVariable);
            warnings.AddRange(resolution.Warnings);
            return new(
                Path.GetFullPath(loaded.Path),
                loaded.Config,
                resolution.Plan,
                resolution.Issues,
                warnings,
                loaded.State,
                inputs);
        }
        catch (Exception exception) when (
            exception is ConfigurationException or
            AudioDeviceSelectionException or
            IOException or
            UnauthorizedAccessException)
        {
            return new(
                configPath,
                null,
                null,
                [new(
                    "config",
                    DiagnosticText.Safe(exception.Message, 1000))],
                [],
                AudioInputs: []);
        }
    }

    public static PipelineViewDefinition PlaceholderTopology() =>
        new(
            PipelineKind.DirectAudioTranslation,
            "Configuration required",
            Array.Empty<PipelineNodeDefinition>(),
            Array.Empty<PipelineEdgeDefinition>());
}
