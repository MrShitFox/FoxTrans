using System.Text;
using Spectre.Console;

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
    Console.Error.WriteLine("Run 'FoxTrans.Cli.exe --help' for usage.");
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
        Console.WriteLine(
            $"Created default {loaded.Path}. Set its API key and run FoxTrans.Cli.exe again.");
        return;
    }
    if (loaded.State == ConfigLoadState.Migrated)
    {
        Console.WriteLine(
            $"Migrated legacy configuration to {loaded.Path}. Review it, then run FoxTrans.Cli.exe again.");
        return;
    }
    foreach (string warning in loaded.Warnings)
        Console.WriteLine($"Configuration warning: {warning}");

    ExecutionPlanResolution resolution = ExecutionPlanResolver.Resolve(
        loaded.Config!,
        new NAudioInputDeviceCatalogue().GetInputs(),
        Environment.GetEnvironmentVariable);
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

    PipelineViewDefinition topology = PipelineTopologyBuilder.Build(plan);
    await using var ui = new PipelineTuiHost(
        topology,
        options.Ui,
        AnsiConsole.Console);
    ui.Report(AppEvent.ConfigWarning(
        $"Selected microphone: device {plan.Audio.DeviceNumber} {plan.Audio.DisplayName}."));
    try
    {
        await using var runtime = new FoxTransRuntime();
        await runtime.StartAsync(plan, ui, shutdown.Token);
        await runtime.Completion.WaitAsync(shutdown.Token);
        if (runtime.State == RuntimeState.Faulted)
            Environment.ExitCode = FoxTransExitCodes.RuntimeFailure;
    }
    catch (Exception exception) when (
        exception is not OperationCanceledException ||
        !shutdown.IsCancellationRequested)
    {
        ui.Report(AppEvent.FatalError(exception.Message));
        throw;
    }
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
