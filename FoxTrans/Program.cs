using System.Text;

Console.OutputEncoding = Encoding.UTF8;
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

CliParseResult parsed = FoxTransCli.Parse(args);
if (!parsed.IsSuccess)
{
    Console.Error.WriteLine($"FoxTrans: {parsed.Error}");
    Console.Error.WriteLine("Run 'FoxTrans.exe --help' for usage.");
    Environment.ExitCode = FoxTransExitCodes.UsageOrConfiguration;
    return;
}

CliOptions options = parsed.Options!;
if (options.Command == FoxTransCommand.Help)
{
    Console.WriteLine(FoxTransCli.HelpText);
    return;
}

try
{
    if (options.Command == FoxTransCommand.Devices)
    {
        Console.WriteLine(AudioDeviceSelection.FormatList(
            new NAudioInputDeviceCatalogue().GetInputs()));
        return;
    }

    ConfigLoadResult loaded = options.ConfigPath is null
        ? AppConfig.LoadOrCreate()
        : AppConfig.LoadExplicit(options.ConfigPath);
    if (loaded.State == ConfigLoadState.Created)
    {
        Console.WriteLine($"Created default {loaded.Path}. Set its API key and run FoxTrans again.");
        return;
    }
    if (loaded.State == ConfigLoadState.Migrated)
    {
        Console.WriteLine($"Migrated legacy configuration to {loaded.Path}. Review it, then run FoxTrans again.");
        return;
    }
    foreach (string warning in loaded.Warnings)
        Console.WriteLine($"Configuration warning: {warning}");

    ExecutionPlanResolution resolution = ExecutionPlanResolver.Resolve(
        loaded.Config!,
        new NAudioInputDeviceCatalogue().GetInputs(),
        Environment.GetEnvironmentVariable);
    foreach (string warning in resolution.Warnings)
        Console.WriteLine($"Configuration warning: {warning}");
    if (!resolution.IsValid)
    {
        foreach (ConfigIssue issue in resolution.Issues)
            Console.Error.WriteLine($"{issue.Path}: {issue.Message}");
        Environment.ExitCode = FoxTransExitCodes.UsageOrConfiguration;
        return;
    }

    ResolvedExecutionPlan plan = resolution.Plan!;
    if (options.DryRun)
    {
        Console.WriteLine(DiagnosticFormatting.FormatPlan(plan));
        Console.WriteLine("No resources were started because --dry-run was used.");
        return;
    }

    if (options.Command == FoxTransCommand.Check)
    {
        using var httpClient = new HttpClient();
        ReadinessCheckResult result = await ReadinessChecks.RunAsync(
            plan,
            httpClient,
            shutdown.Token);
        foreach (string success in result.Successes)
            Console.WriteLine($"[OK] {success}");
        foreach (string failure in result.Failures)
            Console.Error.WriteLine($"[FAIL] {failure}");
        Environment.ExitCode = result.IsReady
            ? FoxTransExitCodes.Success
            : FoxTransExitCodes.DependencyUnavailable;
        return;
    }

    using var ui = new ConsoleUi(plan.Config, plan.Audio);
    ui.Report(AppEvent.ConfigWarning(
        $"Selected microphone: device {plan.Audio.DeviceNumber} {plan.Audio.DisplayName}."));
    await RunAsync(plan, ui, shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    Environment.ExitCode = FoxTransExitCodes.Success;
}
catch (Exception exception) when (
    exception is ConfigurationException or AudioDeviceSelectionException)
{
    Console.Error.WriteLine($"FoxTrans configuration error: {exception.Message}");
    Environment.ExitCode = FoxTransExitCodes.UsageOrConfiguration;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FoxTrans failed: {exception.Message}");
    Environment.ExitCode = FoxTransExitCodes.RuntimeFailure;
}

static async Task RunAsync(
    ResolvedExecutionPlan plan,
    IAppReporter reporter,
    CancellationToken cancellationToken)
{
    var outputs = new List<VrChatOscOutput>(plan.Outputs.Count);
    try
    {
        foreach (ResolvedOscEndpoint output in plan.Outputs)
            outputs.Add(new VrChatOscOutput(output));

        switch (plan.PipelineKind)
        {
            case PipelineKind.DirectAudioTranslation:
            {
                using var microphone = new NAudioMicrophoneSource(plan.Audio);
                using var segmenter = new WebRtcVadSegmenter(plan.Vad!);
                using var httpClient = new HttpClient();
                await FoxTransApp.RunDirectAudioPipelineAsync(
                    microphone,
                    segmenter,
                    new OpenAiAudioTranslator(httpClient, plan.Direct!),
                    outputs,
                    reporter,
                    cancellationToken: cancellationToken);
                break;
            }
            case PipelineKind.BatchTranscriptionTranslation:
            {
                using var microphone = new NAudioMicrophoneSource(plan.Audio);
                using var segmenter = new WebRtcVadSegmenter(plan.Vad!);
                using var httpClient = new HttpClient();
                await FoxTransApp.RunBatchTranscriptionPipelineAsync(
                    microphone,
                    segmenter,
                    new OpenAiTranscriber(httpClient, plan.Transcription!),
                    new OpenAiTextTranslator(httpClient, plan.Translation!),
                    outputs,
                    reporter,
                    cancellationToken: cancellationToken);
                break;
            }
            case PipelineKind.RealtimeTranscriptionTranslation:
            {
                using var httpClient = new HttpClient();
                var healthClient = new VoxtralFoxHealthClient(httpClient);
                VoxtralHealthInfo health = await healthClient.CheckAsync(
                    plan.Voxtral!,
                    cancellationToken);
                reporter.Report(AppEvent.VoxtralHealthChecked(health));
                reporter.Report(AppEvent.VoxtralConnecting(plan.Voxtral!.RealtimeEndpoint));

                using var microphone = new NAudioMicrophoneSource(plan.Audio);
                await using var supervisor = new VoxtralConnectionSupervisor(
                    plan.Audio.Format,
                    (generation, sent) => new VoxtralFoxTranscriber(
                        plan.Voxtral!,
                        connectionGeneration: generation,
                        audioSent: sent),
                    async token =>
                    {
                        _ = await healthClient.CheckAsync(plan.Voxtral!, token);
                    });
                await FoxTransApp.RunRealtimeTranscriptionPipelineAsync(
                    microphone,
                    supervisor,
                    new OpenAiTextTranslator(httpClient, plan.Translation!),
                    outputs,
                    plan.Realtime!,
                    reporter,
                    cancellationToken);
                break;
            }
            default:
                throw new ConfigurationException("No executable pipeline was selected.");
        }
    }
    finally
    {
        foreach (VrChatOscOutput output in outputs)
            await output.DisposeAsync();
    }
}
