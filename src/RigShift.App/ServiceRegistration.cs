using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.App.Views;
using RigShift.App.Views.Pages;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Games;
using Serilog;

namespace RigShift.App;

/// <summary>
/// The tray app's container. A plain one: the app has no hosted services, configuration or Microsoft.Extensions.Logging
/// users, and every service logs through Serilog directly. A test builds it with
/// <c>ValidateOnBuild</c>, so a missing registration fails there instead of when the user opens a page.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddRigShift(this IServiceCollection services, AppPaths paths, IAppShell shell, ILogger log)
    {
        services.AddRigShiftServices(paths, shell, log);

        // What WPF does
        services.AddSingleton<WpfSwitchConfirmation>();
        services.AddSingleton<ISwitchConfirmation>(sp => sp.GetRequiredService<WpfSwitchConfirmation>());
        services.AddSingleton<DisplayChangeWatcher>();
        services.AddSingleton(sp => new HotkeyService(
            sp.GetRequiredService<ProfileCatalog>(),
            sp.GetRequiredService<GameCatalog>(),
            sp.GetRequiredService<GameSessionService>(),
            sp.GetRequiredService<SwitchCoordinator>(),
            sp.GetRequiredService<SettingsService>(),
            log,
            new Win32HotkeyRegistrar(),
            sp.GetRequiredService<WpfSwitchConfirmation>()));
        services.AddSingleton<TrayIconService>();
        services.AddSingleton(sp => new CommandPipeServer(
            sp.GetRequiredService<CommandRunner>(),
            shell,
            log,
            work => System.Windows.Application.Current.Dispatcher.InvokeAsync(work).Task.Unwrap()));
        services.AddSingleton<IAppPicker, WindowAppPicker>();
        services.AddSingleton<ITextClipboard, WpfClipboard>();
        services.AddSingleton<ProfileDialogs>();
        services.AddSingleton<IProfilePageDialogs>(sp => sp.GetRequiredService<ProfileDialogs>());
        services.AddSingleton<IHelpPageDialogs>(sp => sp.GetRequiredService<ProfileDialogs>());
        services.AddSingleton<GameDialogs>();
        services.AddSingleton<IGamePageDialogs>(sp => sp.GetRequiredService<GameDialogs>());

        // Windows and pages
        services.AddSingleton<TrayPopupView>();
        services.AddSingleton<GamesPage>();
        services.AddSingleton<ProfilesPage>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<OverviewPage>();
        services.AddSingleton<AboutPage>();
        services.AddSingleton<FovPage>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
