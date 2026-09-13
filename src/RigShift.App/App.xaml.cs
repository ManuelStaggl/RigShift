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

    public void Quit()
    {
        IsExiting = true;
        Shutdown();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(Paths.DataDirectory);

        Log.Logger = AppLogging.Create(Paths);

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
        services.AddSingleton(_ => new UpdateService(Log.Logger));

        // UI
        services.AddSingleton<TrayPopupViewModel>();
        services.AddSingleton<TrayPopupView>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<ProfilesPage>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<DiagnosticsPage>();
        services.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }
}
