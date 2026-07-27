using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FoxTrans.Desktop.Models;
using FoxTrans.Desktop.Services;

namespace FoxTrans.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly DesktopApplicationServices _services;
    private readonly DesktopEventBridge _bridge;
    private DesktopPage _selectedPage;
    private RuntimeState _runtimeState;
    private string _notificationTitle = "";
    private string _notificationDetail = "";
    private bool _hasNotification;
    private bool _notificationIsError;
    private DateTimeOffset _lastNotificationAt;
    private int _shutdown;

    public MainWindowViewModel(DesktopApplicationServices services)
    {
        _services = services;
        DesktopBootstrapResult bootstrap = services.Bootstrap;
        var bootstrapRedactor = new DesktopSecretRedactor(
            bootstrap.Plan,
            bootstrap.Config);
        PipelineViewDefinition definition = bootstrap.Plan is null
            ? DesktopBootstrap.PlaceholderTopology()
            : PipelineTopologyBuilder.Build(bootstrap.Plan);
        _bridge = new DesktopEventBridge(definition, bootstrap.Plan);
        Live = new(definition, bootstrap.Plan);

        OpenConfigFolderCommand = new RelayCommand(OpenConfigFolder);
        Pipeline = new(bootstrap, definition, OpenConfigFolderCommand);
        Settings = new(services.Preferences, SettingsChanged);

        ToggleRuntimeCommand = new AsyncRelayCommand(
            ToggleRuntimeAsync,
            CanToggleRuntime);
        NavigateLiveCommand = new RelayCommand(
            () => SelectedPage = DesktopPage.Live);
        NavigatePipelineCommand = new RelayCommand(
            () => SelectedPage = DesktopPage.Pipeline);
        NavigateSettingsCommand = new RelayCommand(
            () => SelectedPage = DesktopPage.Settings);
        DismissNotificationCommand = new RelayCommand(
            () => HasNotification = false);

        _selectedPage = services.Preferences.LaunchOnLivePage
            ? DesktopPage.Live
            : services.Preferences.SelectedPage;
        _runtimeState = RuntimeState.Stopped;
        PipelineName = definition.Title;
        HasValidPlan = bootstrap.Plan is not null;

        if (!HasValidPlan)
        {
            ShowNotification(
                "Configuration needs attention",
                bootstrap.Issues.FirstOrDefault() is { } issue
                    ? bootstrapRedactor.Redact(issue.Message)
                    :
                    "Open the Pipeline page to review configuration problems.",
                true,
                DateTimeOffset.UtcNow);
        }
        else if (bootstrap.LoadState == ConfigLoadState.Created)
        {
            ShowNotification(
                "Configuration created",
                $"Review {bootstrap.ConfigPath}, then press Start.",
                false,
                DateTimeOffset.UtcNow);
        }
        else if (bootstrap.Warnings.Count > 0)
        {
            ShowNotification(
                "Configuration warning",
                bootstrapRedactor.Redact(bootstrap.Warnings[0]),
                false,
                DateTimeOffset.UtcNow);
        }
    }

    public event Action<DesktopAppearance>? AppearanceChanged;

    public LiveStudioViewModel Live { get; }
    public PipelinePageViewModel Pipeline { get; }
    public SettingsViewModel Settings { get; }
    public string PipelineName { get; }
    public bool HasValidPlan { get; }

    public IAsyncRelayCommand ToggleRuntimeCommand { get; }
    public IRelayCommand NavigateLiveCommand { get; }
    public IRelayCommand NavigatePipelineCommand { get; }
    public IRelayCommand NavigateSettingsCommand { get; }
    public IRelayCommand OpenConfigFolderCommand { get; }
    public IRelayCommand DismissNotificationCommand { get; }

    public DesktopPage SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (!SetProperty(ref _selectedPage, value))
                return;
            OnPropertyChanged(nameof(IsLiveSelected));
            OnPropertyChanged(nameof(IsPipelineSelected));
            OnPropertyChanged(nameof(IsSettingsSelected));
            _services.Preferences = _services.Preferences with
            {
                SelectedPage = value
            };
        }
    }

    public bool IsLiveSelected => SelectedPage == DesktopPage.Live;
    public bool IsPipelineSelected => SelectedPage == DesktopPage.Pipeline;
    public bool IsSettingsSelected => SelectedPage == DesktopPage.Settings;

    public RuntimeState RuntimeState
    {
        get => _runtimeState;
        private set
        {
            if (!SetProperty(ref _runtimeState, value))
                return;
            OnPropertyChanged(nameof(RuntimeStateText));
            OnPropertyChanged(nameof(PrimaryActionText));
            OnPropertyChanged(nameof(IsRuntimeBusy));
            ToggleRuntimeCommand.NotifyCanExecuteChanged();
        }
    }

    public string RuntimeStateText => RuntimeState switch
    {
        RuntimeState.Stopped => "Ready",
        RuntimeState.Starting => "Starting",
        RuntimeState.Running => "Live",
        RuntimeState.Stopping => "Stopping",
        RuntimeState.Faulted => "Needs attention",
        _ => RuntimeState.ToString()
    };

    public string PrimaryActionText => RuntimeState switch
    {
        RuntimeState.Running => "Stop",
        RuntimeState.Starting => "Starting…",
        RuntimeState.Stopping => "Stopping…",
        RuntimeState.Faulted => "Start again",
        _ => "Start"
    };

    public bool IsRuntimeBusy => RuntimeState is
        RuntimeState.Starting or RuntimeState.Stopping;

    public string NotificationTitle
    {
        get => _notificationTitle;
        private set => SetProperty(ref _notificationTitle, value);
    }

    public string NotificationDetail
    {
        get => _notificationDetail;
        private set => SetProperty(ref _notificationDetail, value);
    }

    public bool HasNotification
    {
        get => _hasNotification;
        private set => SetProperty(ref _hasNotification, value);
    }

    public bool NotificationIsError
    {
        get => _notificationIsError;
        private set => SetProperty(ref _notificationIsError, value);
    }

    public DesktopPreferences Preferences => _services.Preferences;

    public void Tick(DateTimeOffset now, double animationSeconds)
    {
        RuntimeState = _services.Runtime.State;
        DesktopRuntimeSnapshot snapshot =
            DesktopRuntimeReducer.Advance(_bridge.Snapshot, now);
        Live.ReducedMotion = Settings.ReducedMotion;
        Live.Apply(
            snapshot,
            _bridge.LatestAudioFrame,
            now,
            animationSeconds);
        Live.AdvanceText(now);

        if (snapshot.Notification is { } notification &&
            notification.ObservedAt > _lastNotificationAt)
        {
            ShowNotification(
                notification.Title,
                notification.Detail,
                notification.Severity == DesktopNotificationSeverity.Error,
                notification.ObservedAt);
        }
    }

    private bool CanToggleRuntime() =>
        HasValidPlan &&
        RuntimeState is RuntimeState.Stopped or
            RuntimeState.Running or
            RuntimeState.Faulted;

    private async Task ToggleRuntimeAsync()
    {
        try
        {
            RuntimeState state = _services.Runtime.State;
            if (state == RuntimeState.Running)
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(6));
                await _services.Runtime.StopAsync(timeout.Token);
            }
            else if (state is RuntimeState.Stopped or RuntimeState.Faulted &&
                     _services.Bootstrap.Plan is { } plan)
            {
                await _services.Runtime.StartAsync(
                    plan,
                    _bridge,
                    CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            ShowNotification(
                "Could not change runtime state",
                Safe(exception.Message),
                true,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            RuntimeState = _services.Runtime.State;
        }
    }

    private void SettingsChanged()
    {
        _services.Preferences = Settings.ApplyTo(_services.Preferences);
        Live.ReducedMotion = Settings.ReducedMotion;
        AppearanceChanged?.Invoke(Settings.Appearance);
        TrySavePreferences();
    }

    private void OpenConfigFolder()
    {
        try
        {
            string folder = Path.GetDirectoryName(
                Path.GetFullPath(Pipeline.ConfigPath)) ??
                _services.WorkingDirectory;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { folder },
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ShowNotification(
                "Could not open the config folder",
                Safe(exception.Message),
                true,
                DateTimeOffset.UtcNow);
        }
    }

    public void UpdateWindowPreferences(
        double width,
        double height,
        int? x,
        int? y)
    {
        _services.Preferences = Settings.ApplyTo(
            _services.Preferences with
            {
                SelectedPage = SelectedPage,
                WindowWidth = width,
                WindowHeight = height,
                WindowX = x,
                WindowY = y
            });
        TrySavePreferences();
    }

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
            return;
        try
        {
            if (_services.Runtime.State is not RuntimeState.Stopped)
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(6));
                await _services.Runtime.StopAsync(timeout.Token);
            }
        }
        finally
        {
            TrySavePreferences();
            await _bridge.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private void ShowNotification(
        string title,
        string detail,
        bool isError,
        DateTimeOffset observedAt)
    {
        NotificationTitle = title;
        NotificationDetail = Safe(detail);
        NotificationIsError = isError;
        HasNotification = true;
        _lastNotificationAt = observedAt;
    }

    private void TrySavePreferences()
    {
        try
        {
            _services.SavePreferences();
        }
        catch
        {
            // Preferences are non-critical and never interrupt the pipeline.
        }
    }

    private static string Safe(string value)
    {
        string safe = value.Replace('\r', ' ').Replace('\n', ' ');
        return safe.Length <= 800 ? safe : safe[..800] + "…";
    }
}
