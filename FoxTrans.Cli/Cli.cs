public enum FoxTransCommand
{
    Run,
    Check,
    Devices,
    Help
}

public sealed record CliOptions(
    FoxTransCommand Command,
    string? ConfigPath = null,
    bool DryRun = false,
    UiMode Ui = UiMode.Auto);

public sealed record CliParseResult(CliOptions? Options, string? Error = null)
{
    public bool IsSuccess => Options is not null;
}

public static class FoxTransCli
{
    public const string HelpText =
        """
        Usage:
          FoxTrans.Cli.exe [run] [--config PATH] [--dry-run] [--ui auto|rich|plain]
          FoxTrans.Cli.exe check [--config PATH]
          FoxTrans.Cli.exe devices
          FoxTrans.Cli.exe --help

        Commands:
          run       Run the configured translation pipeline (default).
          check     Check configuration, devices, and safe provider readiness.
          devices   List microphone input devices without starting capture.

        Options:
          --config PATH  Use this exact configuration file.
          --dry-run      Print the resolved execution plan without starting resources.
          --ui MODE      Runtime UI: auto (default), rich, or plain.
          -h, --help     Show this help.
        """;

    public static CliParseResult Parse(IReadOnlyList<string> args)
    {
        FoxTransCommand command = FoxTransCommand.Run;
        bool commandSeen = false;
        bool dryRun = false;
        UiMode ui = UiMode.Auto;
        bool uiSeen = false;
        string? configPath = null;

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            if (argument is "-h" or "--help")
            {
                if (args.Count != 1)
                    return Error("Help cannot be combined with another command or option.");
                return new(new(FoxTransCommand.Help));
            }

            if (argument == "--config")
            {
                if (configPath is not null)
                    return Error("--config may be specified only once.");
                if (++index >= args.Count || args[index].StartsWith('-'))
                    return Error("--config requires a file path.");
                configPath = args[index];
                continue;
            }

            if (argument == "--dry-run")
            {
                if (dryRun)
                    return Error("--dry-run may be specified only once.");
                dryRun = true;
                continue;
            }

            if (argument == "--ui")
            {
                if (uiSeen)
                    return Error("--ui may be specified only once.");
                if (++index >= args.Count || args[index].StartsWith('-'))
                    return Error("--ui requires auto, rich, or plain.");
                ui = args[index].ToLowerInvariant() switch
                {
                    "auto" => UiMode.Auto,
                    "rich" => UiMode.Rich,
                    "plain" => UiMode.Plain,
                    _ => (UiMode)(-1)
                };
                if ((int)ui < 0)
                    return Error("--ui requires auto, rich, or plain.");
                uiSeen = true;
                continue;
            }

            if (argument.StartsWith('-'))
                return Error($"Unknown option '{argument}'.");
            if (commandSeen)
                return Error($"Unexpected argument '{argument}'.");

            command = argument.ToLowerInvariant() switch
            {
                "run" => FoxTransCommand.Run,
                "check" => FoxTransCommand.Check,
                "devices" => FoxTransCommand.Devices,
                _ => (FoxTransCommand)(-1)
            };
            if ((int)command < 0)
                return Error($"Unknown command '{argument}'.");
            commandSeen = true;
        }

        if (dryRun && command != FoxTransCommand.Run)
            return Error("--dry-run is valid only with the run command.");
        if (configPath is not null && command == FoxTransCommand.Devices)
            return Error("--config is not valid with the devices command.");
        if (uiSeen && command != FoxTransCommand.Run)
            return Error("--ui is valid only with the run command.");
        return new(new(command, configPath, dryRun, ui));
    }

    private static CliParseResult Error(string message) => new(null, message);
}

public static class FoxTransExitCodes
{
    public const int Success = 0;
    public const int RuntimeFailure = 1;
    public const int UsageOrConfiguration = 2;
    public const int DependencyUnavailable = 3;
}
