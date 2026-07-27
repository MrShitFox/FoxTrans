using CommunityToolkit.Mvvm.ComponentModel;
using FoxTrans.Desktop.Services;

namespace FoxTrans.Desktop.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly Action _changed;
    private DesktopAppearance _appearance;
    private bool _reducedMotion;
    private bool _launchOnLivePage;
    private bool _rememberWindowPlacement;

    public SettingsViewModel(
        DesktopPreferences preferences,
        Action changed)
    {
        _appearance = preferences.Appearance;
        _reducedMotion = preferences.ReducedMotion;
        _launchOnLivePage = preferences.LaunchOnLivePage;
        _rememberWindowPlacement = preferences.RememberWindowPlacement;
        _changed = changed;
    }

    public IReadOnlyList<DesktopAppearance> Appearances { get; } =
        Enum.GetValues<DesktopAppearance>();

    public DesktopAppearance Appearance
    {
        get => _appearance;
        set
        {
            if (SetProperty(ref _appearance, value))
                _changed();
        }
    }

    public bool ReducedMotion
    {
        get => _reducedMotion;
        set
        {
            if (SetProperty(ref _reducedMotion, value))
                _changed();
        }
    }

    public bool LaunchOnLivePage
    {
        get => _launchOnLivePage;
        set
        {
            if (SetProperty(ref _launchOnLivePage, value))
                _changed();
        }
    }

    public bool RememberWindowPlacement
    {
        get => _rememberWindowPlacement;
        set
        {
            if (SetProperty(ref _rememberWindowPlacement, value))
                _changed();
        }
    }

    public DesktopPreferences ApplyTo(DesktopPreferences current) =>
        current with
        {
            Appearance = Appearance,
            ReducedMotion = ReducedMotion,
            LaunchOnLivePage = LaunchOnLivePage,
            RememberWindowPlacement = RememberWindowPlacement
        };
}
