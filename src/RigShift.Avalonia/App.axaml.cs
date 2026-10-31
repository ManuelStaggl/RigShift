using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Avalonia.Services;
using RigShift.Avalonia.Tray;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Avalonia;

/// <summary>The services of the WPF app, the tray and the window with rail and pages.</summary>
public sealed class App(CliRequest request, AppPaths paths) : Application, IAppShell
{
    private ServiceProvider? _services;
    private MainWindow? _window;
    private TrayIconService? _tray;

    internal static IServiceProvider Provider => ((App)Current!)._services ?? throw new InvalidOperationException("Services not built.");

    public bool IsExiting { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        // Screenshots in the light theme: RIGSHIFT_PREVIEW_THEME=Light.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_THEME") == "Light")
        {
            RequestedThemeVariant = ThemeVariant.Light;
        }
#endif
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        Directory.CreateDirectory(paths.DataDirectory);
        Dispatcher.UIThread.UnhandledException += (_, e) => Log.Error(e.Exception, "Unhandled exception on the UI thread");
        LogFileHeader.Enabled = true; // Heads the next line: version, Windows, graphics driver.
        Log.Information("RigShift {Version} (Avalonia) starting, data directory {DataDirectory}", typeof(App).Assembly.GetName().Version, paths.DataDirectory);

        try
        {
            _services = Register(new ServiceCollection()).BuildServiceProvider();
            await Provider.GetRequiredService<SettingsService>().LoadAsync(CancellationToken.None);
            ProfileCatalog catalog = Provider.GetRequiredService<ProfileCatalog>();
            await catalog.ReloadAsync(CancellationToken.None);
            await Provider.GetRequiredService<GameCatalog>().ReloadAsync(CancellationToken.None);

            // The pipe first: a second start or a rigshift:// link waits for it.
            CommandRunner runner = Provider.GetRequiredService<CommandRunner>();
            runner.ProfilesChanged += async (_, _) => await catalog.ReloadAsync(CancellationToken.None);
            CommandPipeServer pipe = Provider.GetRequiredService<CommandPipeServer>();
            pipe.Refused += (_, text) => _tray?.ShowRefusedCommand(text);
            pipe.Start();

            _tray = Provider.GetRequiredService<TrayIconService>();
            _tray.Start();
            Provider.GetRequiredService<AutomationService>().Start();
            Provider.GetRequiredService<GameSessionService>().StartWatching();
            Provider.GetRequiredService<UpdateService>().Start();
            Provider.GetRequiredService<HotkeyService>().Start();

            if (!request.Minimized)
            {
                ShowMainWindow();
            }
#if DEBUG
            Debug.Previews.AfterStart(this, _tray);
#endif
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RigShift failed to start");
            Quit();
        }
    }

    public void ShowMainWindow(AppPage? page = null)
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closing += (_, e) =>
            {
                // Closing the window keeps RigShift in the tray; Quit in the tray menu ends it.
                if (!IsExiting)
                {
                    e.Cancel = true;
                    _window.Hide();
                    OnHiddenToTray();
                }
            };
            _window.Navigate(AppPage.Overview);
        }

        _window.Show();
        _window.Activate();
        if (page is { } shown)
        {
            _window.Navigate(shown);
        }
    }

    internal MainWindow? Window => _window;

    public void IdentifyDisplays(IReadOnlyList<(int Number, string Name, DisplayAssignment Mode)> displays) =>
        Views.IdentifyWindow.ShowAll(displays);

    public void Quit()
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        Log.Information("RigShift exits");
        _window?.Close();
        _services?.Dispose();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>
    /// The first close of the window says where RigShift went: Windows 11 puts a new tray icon behind the "^", and a
    /// window that simply vanishes looks like a closed app. Once, remembered in the settings.
    /// </summary>
    private async void OnHiddenToTray()
    {
        SettingsService settings = Provider.GetRequiredService<SettingsService>();
        if (settings.Current.TrayHintShown)
        {
            return;
        }

        _tray?.ShowKeepsRunningHint();
        try
        {
            await settings.UpdateAsync(s => s with { TrayHintShown = true }, CancellationToken.None, notify: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not remember that the tray hint was shown");
        }
    }

    // shortcut: nothing to ask about until the editor saves (#161); then this asks like the WPF app.
    public void QuitByUser() => Quit();

    private ServiceCollection Register(ServiceCollection services)
    {
        services.AddRigShiftServices(paths, this, Log.Logger);
        services.AddSingleton<ISwitchConfirmation, KeepSwitchConfirmation>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<ITextClipboard, AvaloniaClipboard>();
        services.AddSingleton<IAppPicker, NoAppPicker>();
        services.AddSingleton<ProfileEditorFactory>();
        services.AddSingleton<PendingDialogs>();
        services.AddSingleton<IProfilePageDialogs>(sp => sp.GetRequiredService<PendingDialogs>());
        services.AddSingleton<IHelpPageDialogs>(sp => sp.GetRequiredService<PendingDialogs>());
        services.AddSingleton(sp => new HotkeyService(
            sp.GetRequiredService<ProfileCatalog>(),
            sp.GetRequiredService<GameCatalog>(),
            sp.GetRequiredService<GameSessionService>(),
            sp.GetRequiredService<SwitchCoordinator>(),
            sp.GetRequiredService<SettingsService>(),
            Log.Logger,
            new HotkeyRegistrar()));
        AppIcons.Extract = ProgramIcons.Extract;
        // Pipe requests run on the UI thread, and their awaits stay there.
        services.AddSingleton(sp => new CommandPipeServer(
            sp.GetRequiredService<CommandRunner>(), this, Log.Logger, work => Dispatcher.UIThread.InvokeAsync(work)));
#if DEBUG
        Debug.Previews.Register(services);
#endif
        return services;
    }
}
