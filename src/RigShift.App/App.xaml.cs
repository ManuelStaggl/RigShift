using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.App.Views;
using RigShift.App.Views.Pages;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
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
    private IHost? _host;

    public App(CliRequest request)
    {
        _request = request;
    }

    /// <summary>
    /// <c>%AppData%\RigShift</c>: Velopack installs into <c>%LocalAppData%\RigShift</c> and deletes that folder on
    /// uninstall, so profiles and settings must live elsewhere.
    /// </summary>
    public static AppPaths Paths { get; } = new(Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RigShift")));

    public bool IsExiting { get; private set; }

    private IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started.");

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
            if (_host?.Services.GetService<SwitchCoordinator>() is { IsSwitching: true } coordinator)
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
        if (_host?.Services.GetService<SwitchCoordinator>() is { IsSwitching: true })
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
        Log.Information("RigShift {Version} starting, data directory {DataDirectory}", typeof(App).Assembly.GetName().Version, Paths.DataDirectory);

        try
        {
            ApplyWindowsTheme();

            // Arguments are parsed by CliParser; the host must not interpret them as configuration.
            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices(RegisterServices)
                .Build();
            await _host.StartAsync();

            await Services.GetRequiredService<SettingsService>().LoadAsync(CancellationToken.None);
            ProfileCatalog catalog = Services.GetRequiredService<ProfileCatalog>();
            await catalog.ReloadAsync(CancellationToken.None);

            Services.GetRequiredService<TrayIconService>().Start();
            Services.GetRequiredService<HotkeyService>().Start();
            Services.GetRequiredService<AutomationService>().Start();

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
#endif
            Services.GetRequiredService<DisplayChangeWatcher>().DisplaysChanged +=
                async (_, _) =>
                {
                    await catalog.RefreshActiveAsync(CancellationToken.None);
                    await Services.GetRequiredService<SwitchCoordinator>().CatchUpAsync();
                };

            CommandRunner runner = Services.GetRequiredService<CommandRunner>();
            runner.ProfilesChanged += async (_, _) => await catalog.ReloadAsync(CancellationToken.None);
            Services.GetRequiredService<CommandPipeServer>().Start();
            Services.GetRequiredService<UpdateService>().Start();

            if (!_request.Minimized)
            {
                ShowMainWindow();
            }

            KeepAwakeForActiveProfile(catalog);
            await Services.GetRequiredService<SwitchOrchestrator>().RestoreDuckingIfUnusedAsync(catalog.ActiveProfile, CancellationToken.None);
            await ApplyDefaultProfileAsync(catalog);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RigShift failed to start");
            MessageBox.Show(ex.Message, "RigShift", MessageBoxButton.OK, MessageBoxImage.Error);
            Quit();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("RigShift exiting with code {ExitCode}", e.ApplicationExitCode);
        _host?.Dispose();
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
        // The brand accent replaces the Windows accent, so theme changes must not bring the system accent back.
        ApplicationThemeManager.Changed += (current, _) =>
        {
            Log.Information("App theme changed to {Theme} (Windows reports {SystemTheme})", current, ApplicationThemeManager.GetSystemTheme());
            BrandTheme.Apply(current);
        };
        ApplicationThemeManager.Apply(theme, updateAccent: false);
        BrandTheme.Apply(theme);
    }

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

    private async Task ApplyDefaultProfileAsync(ProfileCatalog catalog)
    {
        AppSettings settings = Services.GetRequiredService<SettingsService>().Current;
        if (settings.ApplyDefaultProfileOnStartup
            && settings.DefaultProfileId is { } id
            && catalog.Find(id) is { } profile
            && catalog.ActiveProfile?.Id != id)
        {
            Log.Information("Applying default profile {Profile} at startup", profile.Name);
            await Services.GetRequiredService<SwitchCoordinator>().SwitchAsync(profile);
        }
    }

    private void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(Log.Logger);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Paths);
        services.AddSingleton<IAppShell>(this);

        // Core and OS boundary
        services.AddSingleton<IProfileStore>(_ => new JsonProfileStore(Paths.Profiles, Log.Logger));
        services.AddSingleton(_ => new JsonSettingsStore(Paths.SettingsFile, Log.Logger));
        services.AddSingleton<IAutostart>(_ => new RunKeyAutostart(Environment.ProcessPath ?? "RigShift.exe", Log.Logger));
        services.AddSingleton<IDisplayConfigurator, CcdDisplayConfigurator>();
        services.AddSingleton<IAudioController, PolicyConfigAudioController>();
        services.AddSingleton<IAppLauncher, Windows.Apps.ProcessAppLauncher>();
        services.AddSingleton<IPowerController, Windows.Power.PowerController>();
        services.AddSingleton<IUsbDeviceList, Windows.Apps.UsbDeviceList>();
        services.AddSingleton<IUsbPowerCheck, Windows.Power.UsbPowerCheck>();
        services.AddSingleton<IDuckingPreference, RegistryDuckingPreference>();
        services.AddSingleton<IDuckingMemory, SettingsDuckingMemory>();
        services.AddSingleton<IWindowRescuer, Windows.Ui.WindowRescuer>();
        services.AddSingleton<ISwitchConfirmation, WpfSwitchConfirmation>();
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
            sp.GetRequiredService<SwitchCoordinator>()));
        services.AddSingleton<CommandPipeServer>();
        services.AddSingleton<ProfileDialogs>();
        services.AddSingleton<UpdateService>();

        // UI
        services.AddSingleton<TrayPopupViewModel>();
        services.AddSingleton<TrayPopupView>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DisplaysViewModel>();
        services.AddSingleton<AutomationViewModel>();
        services.AddSingleton<AboutViewModel>();
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
    }
}
