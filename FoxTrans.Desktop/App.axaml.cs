using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using FoxTrans.Desktop.Services;
using FoxTrans.Desktop.ViewModels;
using FoxTrans.Desktop.Views;

namespace FoxTrans.Desktop;

public sealed partial class App : Application
{
    private DesktopApplicationServices? _services;

    public override void Initialize() =>
        AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = DesktopApplicationServices.Create();
            var viewModel = new MainWindowViewModel(_services);
            ApplyAppearance(_services.Preferences.Appearance);
            viewModel.AppearanceChanged += ApplyAppearance;
            desktop.MainWindow = new MainWindow(viewModel);
            desktop.Exit += async (_, _) =>
            {
                await viewModel.ShutdownAsync();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void ApplyAppearance(DesktopAppearance appearance)
    {
        RequestedThemeVariant = appearance switch
        {
            DesktopAppearance.Dark => ThemeVariant.Dark,
            DesktopAppearance.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
    }
}
