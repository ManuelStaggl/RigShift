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
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;        LogFileHeader.Enabled = true; // Heads the next line: version, Windows, graphics driver.
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
            bool assistantPreview = Debug.Previews.AfterStart(this, _tray, request.PreviewConfirmation);
#else
            const bool assistantPreview = false;
#endif

            // First start: guide through the first two profiles. Posted, so startup finishes before the dialog blocks.
            if (!request.Minimized && !assistantPreview && catalog.Profiles.Count == 0 && !catalog.HasUnreadableFiles
                && !Provider.GetRequiredService<SettingsService>().Current.SetupAssistantShown)
            {
                // Async void on purpose: an exception reaches the UI handler instead of vanishing in a discarded task.
                Dispatcher.UIThread.Post(async () => await Provider.GetRequiredService<PageDialogs>().ShowSetupAssistantAsync());
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RigShift failed to start");
            Quit();
        }
    }

    /// <summary>Set when the user chose "Restart" after a repeated error; <see cref="Program"/> starts RigShift again.</summary>
    internal static bool RestartRequested { get; private set; }

    private readonly UiExceptionTracker _uiExceptions = new(TimeProvider.System);

    private void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Handled in every case: the alternative is the process ending in the middle of whatever the user was doing.
        // What differs is whether we keep quiet about it.
        e.Handled = true;
        UiExceptionVerdict verdict = _uiExceptions.Record(e.Exception);
        if (verdict == UiExceptionVerdict.Quiet)
        {
            // A loop can throw hundreds of times a second; the first ones are in the log with their stack.
            Log.Debug("Unhandled UI exception again: {Message}", e.Exception.Message);
            return;
        }

        Log.Error(e.Exception, "Unhandled UI exception");
        if (verdict == UiExceptionVerdict.Escalate)
        {
            Log.Fatal("The same UI exception keeps coming back, asking the user whether to restart");
            Dispatcher.UIThread.Post(() => _ = AskAboutRepeatedErrorAsync(e.Exception.Message));
            return;
        }

        _tray?.ShowUnexpectedError(e.Exception.Message);
    }

    private async Task AskAboutRepeatedErrorAsync(string message)
    {
        try
        {
            while (true)
            {
                RepeatedErrorChoice choice = await PageDialogs.AskAboutRepeatedErrorAsync(message);
                if (choice == RepeatedErrorChoice.OpenLog)
                {
                    ShellFolders.Open(paths.Logs, Log.Logger);
                    continue;
                }

                if (choice == RepeatedErrorChoice.Restart)
                {
                    RestartRequested = true;
                    Quit();
                    return;
                }

                break;
            }
        }
        catch (Exception ex)
        {
            // The dialog is Avalonia too and may be what is broken; then restarting is the safe way out.
            Log.Error(ex, "The dialog about the repeated error failed, restarting");
            RestartRequested = true;
            Quit();
            return;
        }

        _uiExceptions.Reset();
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

    /// <summary>Owner for dialogs: the main window while it is shown, else none.</summary>
    internal static Window? Owner => ((App)Current!)._window is { IsVisible: true } window ? window : null;

    /// <summary>The owner as a Win32 handle for native dialogs; 0 without one.</summary>
    internal static nint OwnerHandle => Owner?.TryGetPlatformHandle()?.Handle ?? 0;

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
        services.AddSingleton<AvaloniaSwitchConfirmation>();
        services.AddSingleton<ISwitchConfirmation>(sp => sp.GetRequiredService<AvaloniaSwitchConfirmation>());
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<ITextClipboard, AvaloniaClipboard>();
        services.AddSingleton<IAppPicker, Views.AvaloniaAppPicker>();
        services.AddSingleton<ProfileEditorFactory>();
        services.AddSingleton<PageDialogs>();
        services.AddSingleton<IProfilePageDialogs>(sp => sp.GetRequiredService<PageDialogs>());
        services.AddSingleton<IHelpPageDialogs>(sp => sp.GetRequiredService<PageDialogs>());
        services.AddSingleton(sp => new HotkeyService(
            sp.GetRequiredService<ProfileCatalog>(),
            sp.GetRequiredService<GameCatalog>(),
            sp.GetRequiredService<GameSessionService>(),
            sp.GetRequiredService<SwitchCoordinator>(),
            sp.GetRequiredService<SettingsService>(),
            Log.Logger,
            new HotkeyRegistrar(),
            sp.GetRequiredService<AvaloniaSwitchConfirmation>()));
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
