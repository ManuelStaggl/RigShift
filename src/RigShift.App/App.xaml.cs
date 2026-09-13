using System.Globalization;
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
    private IHost? _host;

    public static AppPaths Paths { get; } = new(Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RigShift")));

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

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                Path.Combine(Paths.Logs, "rigshift-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        Log.Information("RigShift {Version} starting, data directory {DataDirectory}", typeof(App).Assembly.GetName().Version, Paths.DataDirectory);

        try
        {
            ApplyWindowsTheme();

            _host = Host.CreateDefaultBuilder(e.Args)
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
            if (e.Args.Contains("--preview-confirmation", StringComparer.OrdinalIgnoreCase))
            {
                var preview = new Core.Profiles.Profile { Id = Guid.Empty, Name = "Preview", Displays = [] };
                ConfirmationResult answer = await ConfirmationWindow.ShowAsync(preview, TimeSpan.FromSeconds(10), CancellationToken.None);
                Log.Information("Confirmation preview answered {Answer}", answer);
            }
#endif
            Services.GetRequiredService<DisplayChangeWatcher>().DisplaysChanged +=
                async (_, _) => await catalog.RefreshActiveAsync(CancellationToken.None);

            if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
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

    private static void ApplyWindowsTheme()
    {
        ApplicationTheme theme = ApplicationThemeManager.GetSystemTheme() switch
        {
            SystemTheme.HC1 or SystemTheme.HC2 or SystemTheme.HCBlack or SystemTheme.HCWhite => ApplicationTheme.HighContrast,
            SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion => ApplicationTheme.Dark,
            _ => ApplicationTheme.Light,
        };
        ApplicationThemeManager.Apply(theme);
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
