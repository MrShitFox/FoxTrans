namespace FoxTrans.Desktop.Services;

public sealed class DesktopApplicationServices : IAsyncDisposable
{
    private int _disposed;

    private DesktopApplicationServices(
        string workingDirectory,
        DesktopPreferencesStore preferencesStore,
        DesktopPreferences preferences,
        DesktopBootstrapResult bootstrap,
        IFoxTransRuntime runtime)
    {
        WorkingDirectory = workingDirectory;
        PreferencesStore = preferencesStore;
        Preferences = preferences;
        Bootstrap = bootstrap;
        Runtime = runtime;
    }

    public string WorkingDirectory { get; }
    public DesktopPreferencesStore PreferencesStore { get; }
    public DesktopPreferences Preferences { get; set; }
    public DesktopBootstrapResult Bootstrap { get; private set; }
    public IFoxTransRuntime Runtime { get; }

    public static DesktopApplicationServices Create(
        string? workingDirectory = null,
        DesktopPreferencesStore? preferencesStore = null,
        IFoxTransRuntime? runtime = null,
        IAudioInputDeviceCatalogue? devices = null,
        Func<string, string?>? environment = null)
    {
        string directory = Path.GetFullPath(
            workingDirectory ?? Environment.CurrentDirectory);
        DesktopPreferencesStore store = preferencesStore ?? new();
        return new(
            directory,
            store,
            store.Load(),
            DesktopBootstrap.Load(directory, devices, environment),
            runtime ?? new FoxTransRuntime());
    }

    public DesktopBootstrapResult Reload(
        IAudioInputDeviceCatalogue? devices = null,
        Func<string, string?>? environment = null)
    {
        Bootstrap = DesktopBootstrap.Load(
            WorkingDirectory,
            devices,
            environment);
        return Bootstrap;
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
