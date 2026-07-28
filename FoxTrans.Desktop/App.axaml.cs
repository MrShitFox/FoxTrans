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
            _services = DesktopApplicationServices.Create(
                deferBootstrap: true);
            ApplyAppearance(_services.Preferences.Appearance);
            string? preview = desktop.Args?
                .FirstOrDefault(argument =>
                    argument.StartsWith(
                        "--ui-preview=",
                        StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)
                .ElementAtOrDefault(1);
            string? capturePath = desktop.Args?
                .FirstOrDefault(argument =>
                    argument.StartsWith(
                        "--ui-capture=",
                        StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)
                .ElementAtOrDefault(1);
            var viewModel = new MainWindowViewModel(
                _services,
                preview,
                capturePath);
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
