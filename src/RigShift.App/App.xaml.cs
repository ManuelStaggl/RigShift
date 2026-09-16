using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
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
using Wpf.Ui.Appearance;

namespace RigShift.App;

public partial class App : Application, IAppShell
{
    private readonly CliRequest _request;
    private ServiceProvider? _services;
    private TrayIconService? _tray;

    public App(CliRequest request)
    {
        _request = request;
    }

    /// <summary>
    /// <c>%AppData%\RigShift</c>: Velopack installs into <c>%LocalAppData%\RigShift</c> and deletes that folder on
    /// uninstall, so profiles and settings must live elsewhere.
    /// </summary>
    public static AppPaths Paths { get; } = new(Path.GetFullPath(
#if DEBUG
        // Developer aid: screenshots of steps that save profiles, without touching the real data.
        Environment.GetEnvironmentVariable("RIGSHIFT_DATA_DIR") is { Length: > 0 } previewData ? previewData :
#endif
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RigShift")));

    public bool IsExiting { get; private set; }

    private IServiceProvider Services => _services ?? throw new InvalidOperationException("Services not built.");

    public void ShowMainWindow(Type? page = null)
    {
        MainWindow window = Services.GetRequiredService<MainWindow>();
        window.ShowPage(page);
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>
    /// Ends the app. A running switch is cancelled first and may roll back for up to 30 s, so displays, audio and the
    /// ducking setting are never left half-switched (analysis finding B-02).
    /// </summary>
    public void Quit()
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        _ = QuitAsync();
    }

    private async Task QuitAsync()
    {
        try
        {
            if (_services?.GetService<SwitchCoordinator>() is { IsSwitching: true } coordinator)
            {
                Log.Information("Exit requested during a switch, cancelling it first");
                if (!await coordinator.StopAsync(TimeSpan.FromSeconds(30)))
                {
                    Log.Warning("The switch did not end within 30 s, exiting anyway");
                }
            }
        }
        finally
        {
            Shutdown();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnSessionEnding(e);

        // Blocking here would deadlock the rollback (the countdown window needs this thread), so the session end is
        // refused once while the switch rolls back; the app exits right after.
        if (_services?.GetService<SwitchCoordinator>() is { IsSwitching: true })
        {
            Log.Warning("Windows session ending ({Reason}) during a switch, refusing until it has rolled back", e.ReasonSessionEnding);
            e.Cancel = true;
            Quit();
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(Paths.DataDirectory);

        // Log.Logger was created in Program.Main, before Velopack ran.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        Controls.WheelScrolling.Register();
        Log.Information("RigShift {Version} starting, data directory {DataDirectory}", typeof(App).Assembly.GetName().Version, Paths.DataDirectory);

        try
        {
            ApplyWindowsTheme();

            // A plain container: the app has no hosted services, configuration or Microsoft.Extensions.Logging users,
            // and every service logs through Serilog directly (analysis finding A-05).
            var services = new ServiceCollection();
            RegisterServices(services);
            _services = services.BuildServiceProvider();

            await Services.GetRequiredService<SettingsService>().LoadAsync(CancellationToken.None);
            ProfileCatalog catalog = Services.GetRequiredService<ProfileCatalog>();
            await catalog.ReloadAsync(CancellationToken.None);

            _tray = Services.GetRequiredService<TrayIconService>();
            _tray.Start();
            Services.GetRequiredService<HotkeyService>().Start();
            Services.GetRequiredService<AutomationService>().Start();

            // Games are loaded before the page is opened: the watcher for games started elsewhere needs them, and a
            // game that already runs must only set the starting point, not trigger a switch.
            await Services.GetRequiredService<GameCatalog>().ReloadAsync(CancellationToken.None);
            Services.GetRequiredService<GameSessionService>().StartWatching();

#if DEBUG
            // Developer aid: the confirmation window cannot be reached on a machine without the profile's displays.
            if (_request.PreviewConfirmation)
            {
                var preview = new Core.Profiles.Profile { Id = Guid.Empty, Name = "Preview", Displays = [] };
                ConfirmationResult answer = await ConfirmationWindow.ShowAsync(preview, TimeSpan.FromSeconds(10), CancellationToken.None);
                Log.Information("Confirmation preview answered {Answer}", answer);
            }

            // Developer aid: the tray popup and the tray icon for other taskbars and DPI steps cannot be captured over RDP.
            if (_request.PreviewBranding is { } previewDirectory)
            {
                BrandingPreview.Show(previewDirectory, Services.GetRequiredService<TrayPopupViewModel>());
            }

            // Developer aid: the app picker with a demo list (JSON array of name, path, isRunning) for README screenshots.
            if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_APPS") is { Length: > 0 } demoApps)
            {
                AppPickerWindow.Pick(null, null, () => System.Text.Json.JsonSerializer.Deserialize<List<Windows.Apps.DiscoveredApp>>(
                    System.IO.File.ReadAllText(demoApps), PreviewJson) ?? []);
            }

            // Developer aid: the game picker and the window capture, which otherwise only open from inside the editor.
            // "multi" opens the picker the way the games page does, with tick boxes for several games at once.
            if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_GAMEPICKER") is { Length: > 0 } picker)
            {
                GameDialogs dialogs = Services.GetRequiredService<GameDialogs>();
                if (string.Equals(picker, "multi", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Views.GamePickerWindow.PickManyAsync(null, dialogs);
                }
                else
                {
                    _ = Views.GamePickerWindow.PickAsync(null, dialogs);
                }
            }

            if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_WINDOWCAPTURE") is { Length: > 0 })
            {
                _ = Dispatcher.InvokeAsync(() =>
                    Views.WindowCaptureWindow.Capture(null, Services.GetRequiredService<GameDialogs>(), null));
            }
#endif
            Services.GetRequiredService<DisplayChangeWatcher>().DisplaysChanged +=
                async (_, _) =>
                {
                    Core.Profiles.Profile? before = catalog.ActiveProfile;
                    SwitchCoordinator coordinator = Services.GetRequiredService<SwitchCoordinator>();
                    await catalog.RefreshActiveAsync(CancellationToken.None);
                    await coordinator.CatchUpAsync();
                    coordinator.NoticeDisplayChange(before, catalog.ActiveProfile);
                };

            CommandRunner runner = Services.GetRequiredService<CommandRunner>();
            runner.ProfilesChanged += async (_, _) => await catalog.ReloadAsync(CancellationToken.None);
            Services.GetRequiredService<CommandPipeServer>().Start();
            Services.GetRequiredService<UpdateService>().Start();

            if (!_request.Minimized)
            {
                ShowMainWindow();

                // First start: guide through the first two profiles. Queued, so startup finishes before the dialog blocks.
#if DEBUG
                // Developer aid: the assistant at a later step with demo profiles (second, trigger, done).
                if (Enum.TryParse(Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_SETUP"), ignoreCase: true, out SetupStep previewStep))
                {
                    _ = Dispatcher.InvokeAsync(() => Services.GetRequiredService<ProfileDialogs>().ShowSetupAssistantAsync(previewStep));
                }
                else
#endif
                    if (catalog.Profiles.Count == 0 && !catalog.HasUnreadableFiles && !Services.GetRequiredService<SettingsService>().Current.SetupAssistantShown)
                    {
                        _ = Dispatcher.InvokeAsync(() => Services.GetRequiredService<ProfileDialogs>().ShowSetupAssistantAsync());
                    }
            }

            KeepAwakeForActiveProfile(catalog);
            await OfferInterruptedRestoreAsync();
            await Services.GetRequiredService<SwitchOrchestrator>().RestoreDuckingIfUnusedAsync(catalog.ActiveProfile, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RigShift failed to start");
            MessageBox.Show(ex.Message, "RigShift", MessageBoxButton.OK, MessageBoxImage.Error);
            Quit();
        }
    }

    /// <summary>
    /// A switch that was recorded but never finished means RigShift died between changing the screens and the
    /// confirmation – the one case the in-process rollback cannot cover. Offer the way back, queued so startup finishes
    /// before the question blocks.
    /// </summary>
    private async Task OfferInterruptedRestoreAsync()
    {
        ISwitchJournal journal = Services.GetRequiredService<ISwitchJournal>();
        if (await journal.ReadAsync(CancellationToken.None) is not { } interrupted)
        {
            return;
        }

        Log.Warning("Found an unfinished switch to {Profile} started {Started:u}", interrupted.TargetProfileName, interrupted.StartedUtc);
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (await ProfileDialogs.ConfirmRestoreInterruptedAsync(interrupted.TargetProfileName))
            {
                // Without asking again: the user just answered, and a second countdown on top would only confuse.
                await Services.GetRequiredService<SwitchCoordinator>()
                    .SwitchAsync(interrupted.Previous, new SwitchRequest { SkipConfirmation = true });
            }
            else
            {
                await journal.ClearAsync(CancellationToken.None);
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("RigShift exiting with code {ExitCode}", e.ApplicationExitCode);
        // Disposes the singletons in reverse creation order; all of them are IDisposable (none async-only).
        _services?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void ApplyWindowsTheme()
    {
        ApplicationTheme theme = ApplicationThemeManager.GetSystemTheme() switch
        {
            SystemTheme.HC1 or SystemTheme.HC2 or SystemTheme.HCBlack or SystemTheme.HCWhite => ApplicationTheme.HighContrast,
            SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion => ApplicationTheme.Dark,
            _ => ApplicationTheme.Light,
        };

        // Debug builds only: --preview-theme for screenshots; ignored in release builds.
        theme = _request.PreviewTheme switch
        {
#if DEBUG
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
#endif
            _ => theme,
        };
        // BrandTheme applies the Windows accent after each theme change. Every open window's theme watcher reports the same
        // change (after an RDP reconnect twice in the log, finding R-01), so a repeat is skipped. High contrast is always
        // applied: its colors can change without the theme changing.
        ApplicationTheme applied = theme;
        ApplicationThemeManager.Changed += (current, _) =>
        {
#if DEBUG
            // Each window's theme watcher applies the Windows theme when it attaches; a forced preview theme wins.
            if (_request.PreviewTheme is "light" or "dark" && current != theme)
            {
                ApplicationThemeManager.Apply(theme, updateAccent: false);
                return;
            }
#endif
            if (current == applied && current != ApplicationTheme.HighContrast)
            {
                Log.Debug("App theme {Theme} reported again, nothing to do", current);
                return;
            }

            applied = current;
            Log.Information("App theme changed to {Theme} (Windows reports {SystemTheme})", current, ApplicationThemeManager.GetSystemTheme());
            BrandTheme.Apply(current);
        };
        ApplicationThemeManager.Apply(theme, updateAccent: false);
        BrandTheme.Apply(theme);
    }

#if DEBUG
    private static readonly System.Text.Json.JsonSerializerOptions PreviewJson = new(System.Text.Json.JsonSerializerDefaults.Web);

#endif
    /// <summary>The keep-awake request ends with the process; after a restart it follows the profile that is still active.</summary>
    private void KeepAwakeForActiveProfile(ProfileCatalog catalog)
    {
        if (catalog.ActiveProfile is not { KeepAwake: true } active)
        {
            return;
        }

        try
        {
            Log.Information("Active profile {Profile} keeps the PC awake", active.Name);
            Services.GetRequiredService<IPowerController>().SetKeepAwake(true);
        }
        catch (Win32Exception ex)
        {
            Log.Warning(ex, "Keep-awake for {Profile} could not be set at startup", active.Name);
        }
    }

    private void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(Log.Logger);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Paths);
        services.AddSingleton<IAppShell>(this);

        // Core and OS boundary
        services.AddSingleton<IProfileStore>(sp => new JsonProfileStore(Paths.Profiles, Log.Logger, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(_ => new JsonSettingsStore(Paths.SettingsFile, Log.Logger));
        services.AddSingleton<IAutostart>(_ => new RunKeyAutostart(Environment.ProcessPath ?? "RigShift.exe", Log.Logger));
        services.AddSingleton<IDisplayConfigurator, CcdDisplayConfigurator>();
        services.AddSingleton<IAudioController, PolicyConfigAudioController>();
        services.AddSingleton<IAppLauncher, Windows.Apps.ProcessAppLauncher>();
        services.AddSingleton<IPowerController, Windows.Power.PowerController>();
        services.AddSingleton<IUsbDeviceList, Windows.Apps.UsbDeviceList>();
        services.AddSingleton<IUsbPowerCheck, Windows.Power.UsbPowerCheck>();
        services.AddSingleton<IFullscreenCheck, Windows.Shell.ShellFullscreenCheck>();
        services.AddSingleton<IDuckingPreference, RegistryDuckingPreference>();
        services.AddSingleton<IDuckingMemory, SettingsDuckingMemory>();
        services.AddSingleton<IWindowRescuer, Windows.Ui.WindowRescuer>();
        services.AddSingleton<ISurroundController, NvSurroundController>();
        services.AddSingleton<ISwitchConfirmation, WpfSwitchConfirmation>();
        services.AddSingleton<ISwitchJournal>(_ => new JsonSwitchJournal(Paths.DataDirectory, Log.Logger));
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
        services.AddSingleton<AutomationService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton(sp => new CommandRunner(
            sp.GetRequiredService<IProfileStore>(),
            sp.GetRequiredService<IDisplayConfigurator>(),
            sp.GetRequiredService<IAudioController>(),
            sp.GetRequiredService<ActiveProfileMatcher>(),
            Log.Logger,
            sp.GetRequiredService<SwitchCoordinator>(),
            sp.GetRequiredService<ISurroundController>(),
            sp.GetRequiredService<IGameStore>(),
            sp.GetRequiredService<GameSessionService>()));
        services.AddSingleton<CommandPipeServer>();
        services.AddSingleton<ProfileDialogs>();
        services.AddSingleton<UpdateService>();

        // Games (v2)
        services.AddSingleton<IGameStore>(sp => new JsonGameStore(Paths.DataDirectory, Log.Logger, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IGameLibrary, Windows.Games.GameLibrary>();
        services.AddSingleton<IGameProcesses, Windows.Games.SystemGameProcesses>();
        services.AddSingleton<IGameStarter, Windows.Games.ShellGameStarter>();
        services.AddSingleton<IWindowLayout, Windows.Ui.WindowLayoutManager>();
        services.AddSingleton<GameCatalog>();
        services.AddSingleton<GameDialogs>();

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
            Log.Logger,
            sp.GetRequiredService<IWindowLayout>()));
        services.AddSingleton<GameSessionService>();

        // UI
        services.AddSingleton<TrayPopupViewModel>();
        services.AddSingleton<TrayPopupView>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DisplaysViewModel>();
        services.AddSingleton<AutomationViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<GamesViewModel>();
        services.AddSingleton<GamesPage>();
        services.AddSingleton<ProfilesPage>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<DisplaysPage>();
        services.AddSingleton<AutomationPage>();
        services.AddSingleton<AboutPage>();
        services.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;

        // Only once the tray exists: resolving it here during a failed startup could throw again.
        _tray?.ShowUnexpectedError(e.Exception.Message);
    }
}
