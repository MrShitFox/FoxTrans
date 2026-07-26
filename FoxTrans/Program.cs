using System.Text;

Console.OutputEncoding = Encoding.UTF8;

using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
Console.CancelKeyPress += cancelHandler;

ConsoleUi? ui = null;

try
{
    ConfigLoadResult configResult = AppConfig.LoadOrCreate();
    ui = new ConsoleUi(configResult.Config);

    if (configResult.WasCreated)
    {
        ui.Report(AppEvent.ConfigCreated(configResult.Path));
        return;
    }

    AppConfig config = configResult.Config;
    var format = new AudioFormat(16000, 16, 1);

    using var microphone = new NAudioMicrophoneSource(format);
    using var segmenter = new WebRtcVadSegmenter(config.Vad);
    using var httpClient = new HttpClient();
    var translator = new OpenAiAudioTranslator(httpClient, config.Api);
    await using var osc = new VrChatOscOutput(config.Osc);

    await FoxTransApp.RunDirectAudioPipelineAsync(
        microphone,
        segmenter,
        translator,
        [osc],
        ui,
        cancellationToken: shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    Environment.ExitCode = 0;
}
catch (Exception exception)
{
    if (ui is null)
    {
        Console.Error.WriteLine($"FoxTrans failed: {exception.Message}");
    }
    else
    {
        ui.Report(AppEvent.FatalError(exception.Message));
    }

    Environment.ExitCode = 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
    ui?.Dispose();
}
