namespace FoxTrans.Desktop.Services;

public sealed class DesktopApplicationServices : IAsyncDisposable
{
    private int _disposed;

    private DesktopApplicationServices(
        string workingDirectory,
        DesktopPreferencesStore preferencesStore,
        DesktopPreferences preferences,
        DesktopBootstrapResult bootstrap,
        IFoxTransRuntime runtime,
        IAudioInputDeviceCatalogue devices,
        IAudioCaptureFactory audioCapture,
        Func<string, string?> environment)
    {
        WorkingDirectory = workingDirectory;
        PreferencesStore = preferencesStore;
        Preferences = preferences;
        Bootstrap = bootstrap;
        Runtime = runtime;
        Devices = devices;
        AudioCapture = audioCapture;
        Environment = environment;
        Configuration = new(workingDirectory, devices, environment);
    }

    public string WorkingDirectory { get; }
    public DesktopPreferencesStore PreferencesStore { get; }
    public DesktopPreferences Preferences { get; set; }
    public DesktopBootstrapResult Bootstrap { get; private set; }
    public IFoxTransRuntime Runtime { get; }
    public IAudioInputDeviceCatalogue Devices { get; }
    public IAudioCaptureFactory AudioCapture { get; }
    public Func<string, string?> Environment { get; }
    public DesktopConfigurationStore Configuration { get; }

    public static DesktopApplicationServices Create(
        string? workingDirectory = null,
        DesktopPreferencesStore? preferencesStore = null,
        IFoxTransRuntime? runtime = null,
        IAudioInputDeviceCatalogue? devices = null,
        Func<string, string?>? environment = null,
        bool deferBootstrap = false,
        IAudioCaptureFactory? audioCapture = null)
    {
        string directory = Path.GetFullPath(
            workingDirectory ?? System.Environment.CurrentDirectory);
        DesktopPreferencesStore store = preferencesStore ?? new();
        IAudioInputDeviceCatalogue catalogue =
            devices ?? new NativeAudioInputDeviceCatalogue();
        IAudioCaptureFactory capture =
            audioCapture ?? new NativeAudioCaptureFactory();
        Func<string, string?> getEnvironment =
            environment ?? System.Environment.GetEnvironmentVariable;
        DesktopBootstrapResult bootstrap = deferBootstrap
            ? DesktopBootstrap.Pending(directory)
            : DesktopBootstrap.Load(
                directory,
                catalogue,
                getEnvironment);
        return new(
            directory,
            store,
            store.Load(),
            bootstrap,
            runtime ?? new FoxTransRuntime(audioCaptureFactory: capture),
            catalogue,
            capture,
            getEnvironment);
    }

    public DesktopBootstrapResult Reload(
        IAudioInputDeviceCatalogue? devices = null,
        Func<string, string?>? environment = null)
    {
        Bootstrap = DesktopBootstrap.Load(
            WorkingDirectory,
            devices ?? Devices,
            environment ?? Environment);
        return Bootstrap;
    }

    public void AcceptBootstrap(DesktopBootstrapResult bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        Bootstrap = bootstrap;
    }

    public void SavePreferences() =>
        PreferencesStore.Save(Preferences);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await Runtime.DisposeAsync().ConfigureAwait(false);
    }
}
