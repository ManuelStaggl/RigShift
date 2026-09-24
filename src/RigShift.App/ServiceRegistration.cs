using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.App.Views;
using RigShift.App.Views.Pages;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Games;
using RigShift.Core.Settings;
using RigShift.Core.Storage;
using RigShift.Core.Topology;
using RigShift.Windows.Audio;
using RigShift.Windows.Display;
using RigShift.Windows.Startup;
using Serilog;

namespace RigShift.App;

/// <summary>
/// The tray app's container. A plain one: the app has no hosted services, configuration or Microsoft.Extensions.Logging
/// users, and every service logs through Serilog directly (analysis finding A-05). A test builds it with
/// <c>ValidateOnBuild</c>, so a missing registration fails there instead of when the user opens a page.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddRigShift(this IServiceCollection services, AppPaths paths, IAppShell shell, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(log);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(paths);
        services.AddSingleton(shell);

        // Core and OS boundary
        services.AddSingleton<IProfileStore>(sp => new JsonProfileStore(paths.Profiles, log, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(_ => new JsonSettingsStore(paths.SettingsFile, log));
        services.AddSingleton<IAutostart>(_ => new RunKeyAutostart(Environment.ProcessPath ?? "RigShift.exe", log));
        // Everyone shares the guard, so a call stuck in the driver makes the pages fail fast too, not just the switch (K-07).
        services.AddSingleton<IDisplayConfigurator>(sp => new HungDriverGuard(
            new CcdDisplayConfigurator(log, sp.GetRequiredService<TimeProvider>()), sp.GetRequiredService<SwitchOptions>(), sp.GetRequiredService<TimeProvider>(), log));
        services.AddSingleton<IAudioController, PolicyConfigAudioController>();
        services.AddSingleton<IAppLauncher, Windows.Apps.ProcessAppLauncher>();
        services.AddSingleton<IPowerController, Windows.Power.PowerController>();
        services.AddSingleton<IUsbDeviceList, Windows.Apps.UsbDeviceList>();
        services.AddSingleton<IUsbPowerCheck, Windows.Power.UsbPowerCheck>();
        services.AddSingleton<IFullscreenCheck, Windows.Shell.ShellFullscreenCheck>();
        services.AddSingleton<IDuckingPreference, RegistryDuckingPreference>();
        services.AddSingleton<IDuckingMemory, SettingsDuckingMemory>();
        services.AddSingleton<IWindowRescuer, Windows.Ui.WindowRescuer>();
        services.AddSingleton<IDesktopIcons, Windows.Shell.DesktopIcons>();
        services.AddSingleton<IDisplaySizeReader, EdidDisplaySizeReader>();
        services.AddSingleton<ISurroundController, NvSurroundController>();
        services.AddSingleton<ISwitchConfirmation, WpfSwitchConfirmation>();
        services.AddSingleton<ISwitchJournal>(_ => new JsonSwitchJournal(paths.DataDirectory, log));
        services.AddSingleton(new TopologyPlannerOptions());
        services.AddSingleton(new SwitchOptions());
        services.AddSingleton<TopologyPlanner>();
        services.AddSingleton<ActiveProfileMatcher>();
        services.AddSingleton<SwitchOrchestrator>();

        // App services
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ProfileCatalog>();
        services.AddSingleton<SwitchCoordinator>();
        services.AddSingleton<DisplayChangeWatcher>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<ISessionWatch>(_ => new SystemSessionWatch(log));
        services.AddSingleton<AutomationService>();
        services.AddSingleton<TrayIconService>();

        // By hand: the runner's later parameters are optional (the command line runs it without the tray app), and the
        // container would quietly pass null for any service it cannot find.
        services.AddSingleton(sp => new CommandRunner(
            sp.GetRequiredService<IProfileStore>(),
            sp.GetRequiredService<IDisplayConfigurator>(),
            sp.GetRequiredService<IAudioController>(),
            sp.GetRequiredService<ActiveProfileMatcher>(),
            log,
            sp.GetRequiredService<SwitchCoordinator>(),
            sp.GetRequiredService<ISurroundController>(),
            sp.GetRequiredService<IGameStore>(),
            sp.GetRequiredService<GameSessionService>(),
            sp.GetRequiredService<IDesktopIcons>()));
        services.AddSingleton<CommandPipeServer>();
        services.AddSingleton<IAppPicker, WindowAppPicker>();
        services.AddSingleton<ProfileEditorServices>();
        services.AddSingleton<ProfileDialogs>();
        services.AddSingleton<IProfilePageDialogs>(sp => sp.GetRequiredService<ProfileDialogs>());
        services.AddSingleton<IUpdateFeed>(_ => new VelopackUpdateFeed(UpdateService.RepositoryUrl));
        services.AddSingleton<IUpdatePolicy>(_ => new RegistryUpdatePolicy(log));
        services.AddSingleton<UpdateService>();

        // Games (v2)
        services.AddSingleton<IGameStore>(sp => new JsonGameStore(paths.DataDirectory, log, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IGameLibrary, Windows.Games.GameLibrary>();
        services.AddSingleton<IGameProcesses, Windows.Games.SystemGameProcesses>();
        services.AddSingleton<IGameStarter, Windows.Games.ShellGameStarter>();
        services.AddSingleton<IWindowLayout, Windows.Ui.WindowLayoutManager>();
        services.AddSingleton<GameCatalog>();
        services.AddSingleton<GameEditorServices>();
        services.AddSingleton<GameDialogs>();
        services.AddSingleton<IGamePageDialogs>(sp => sp.GetRequiredService<GameDialogs>());

        // A new runner per session: it keeps the state of exactly one run.
        services.AddSingleton<Func<GameSessionRunner>>(sp => () => new GameSessionRunner(
            sp.GetRequiredService<IGameStarter>(),
            sp.GetRequiredService<IGameProcesses>(),
            sp.GetRequiredService<SwitchCoordinator>(),
            id => sp.GetRequiredService<ProfileCatalog>().Find(id),
            sp.GetRequiredService<IAppLauncher>(),
            sp.GetRequiredService<IUsbDeviceList>(),
            sp.GetRequiredService<SwitchOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            log,
            sp.GetRequiredService<IWindowLayout>()));
        services.AddSingleton<GameSessionService>();

        // UI
        services.AddSingleton<TrayPopupViewModel>();
        services.AddSingleton<TrayPopupView>();
        services.AddSingleton<ProfilesViewModel>();

        // The tray popup only needs the page view model when "save arrangement" is clicked.
        services.AddSingleton<Func<ProfilesViewModel>>(sp => sp.GetRequiredService<ProfilesViewModel>);
        services.AddSingleton<UsbDevicesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<FovViewModel>();
        services.AddSingleton<GamesViewModel>();
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
