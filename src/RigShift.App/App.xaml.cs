using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.App.Views;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Topology;
using Serilog;
using Wpf.Ui.Appearance;

namespace RigShift.App;

public partial class App : Application, IAppShell
{
    private readonly CliRequest _request;
    private readonly AppPaths _paths;
    private ServiceProvider? _services;
    private TrayIconService? _tray;

    public App(CliRequest request, AppPaths paths)
    {
        _request = request;
        _paths = paths;

        // Tooltips a little sooner than the Windows default (B-10); the look is the implicit style in Controls.xaml.
        System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(400));
        Controls.Interaction.TrackKeyboardFocus();
    }

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

    public void QuitByUser() => _ = QuitByUserAsync();

    private async Task QuitByUserAsync()
    {
        try
        {
            var profiles = Services.GetRequiredService<ProfilesViewModel>();
            var games = Services.GetRequiredService<GamesViewModel>();
            if (profiles.Editor is { IsDirty: true } || games.Editor is { IsDirty: true })
            {
                // The question needs its window: the tray menu also works while the main window is hidden.
                Log.Information("Exit requested with unsaved changes, asking first");
                ShowMainWindow();
                if (!await profiles.ConfirmLeaveAsync() || !await games.ConfirmLeaveAsync())
                {
                    Log.Information("Exit cancelled, the unsaved changes stay open");
                    return;
                }
            }

            // Without RigShift nothing switches back when the game ends (v4 findings A-14, E-06).
            GameCatalog catalog = Services.GetRequiredService<GameCatalog>();
            string running = string.Join(", ", Services.GetRequiredService<GameSessionService>().RunningGames
                .Select(id => catalog.Find(id)?.Name).OfType<string>());
            if (running.Length > 0)
            {
                Log.Information("Exit requested while {Games} runs, asking first", running);
                if (!await ProfileDialogs.ConfirmQuitDuringGameAsync(running))
                {
                    Log.Information("Exit cancelled, the game session goes on");
                    return;
                }
            }
        }
#pragma warning disable CA1031 // A question that cannot be asked must not make RigShift impossible to exit.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Error(ex, "Asking about unsaved changes before exiting failed, exiting anyway");
        }

        Quit();
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
        Directory.CreateDirectory(_paths.DataDirectory);

        // Log.Logger was created in Program.Main, before Velopack ran.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        Controls.WheelScrolling.Register();
        Log.Information("RigShift {Version} starting, data directory {DataDirectory}", typeof(App).Assembly.GetName().Version, _paths.DataDirectory);

        try
        {
            ApplyWindowsTheme();
#if DEBUG
            if (WriteGalleryPreview())
            {
                Shutdown();
                return;
            }
#endif

            IServiceCollection services = new ServiceCollection().AddRigShift(_paths, this, Log.Logger);
#if DEBUG
            AddPreviewServices(services);
#endif
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
            await StartPreviewsAsync(catalog);
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
#if DEBUG
                bool assistantPreview = StartWindowPreviews();
#else
                const bool assistantPreview = false;
#endif

                // First start: guide through the first two profiles. Queued, so startup finishes before the dialog blocks.
                if (!assistantPreview && catalog.Profiles.Count == 0 && !catalog.HasUnreadableFiles
                    && !Services.GetRequiredService<SettingsService>().Current.SetupAssistantShown)
                {
                    _ = Dispatcher.InvokeAsync(() => Services.GetRequiredService<ProfileDialogs>().ShowSetupAssistantAsync());
                }
            }

            KeepAwakeForActiveProfile(catalog);
            ReportSettingsProblem();
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

    /// <summary>
    /// Defaults in place of the user's settings are not something to find out by accident. Queued, so startup finishes
    /// before the dialog blocks.
    /// </summary>
    private void ReportSettingsProblem()
    {
        Core.Settings.SettingsLoadReport report = Services.GetRequiredService<SettingsService>().LastLoad;
        if (report.Problem == Core.Settings.SettingsLoadProblem.None)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (await ProfileDialogs.ShowSettingsProblemAsync(report))
            {
                ShellFolders.Open(_paths.DataDirectory, Log.Logger);
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("RigShift exiting with code {ExitCode}", e.ApplicationExitCode);
        // Disposes the singletons in reverse creation order; all of them are IDisposable (none async-only).
        _services?.Dispose();

        // The log stays open: Program.Main closes it, after a restart that may still have something to say.
        base.OnExit(e);
    }

    private static void ApplyWindowsTheme()
    {
        // One theme, dark; only high contrast comes from Windows. A window's theme watcher may still report the light
        // theme – it is put back. Every open window's watcher reports the same change (after an RDP reconnect twice in the
        // log, finding R-01), so a repeat is skipped. High contrast is always applied: its colors can change without the
        // theme changing.
        ApplicationTheme applied = ThemeFor(ApplicationThemeManager.GetSystemTheme());
        ApplicationThemeManager.Changed += (current, _) =>
        {
            ApplicationTheme wanted = ThemeFor(ApplicationThemeManager.GetSystemTheme());
            if (current != wanted)
            {
                Log.Debug("App theme {Theme} reported, applying {Wanted} instead", current, wanted);
                ApplicationThemeManager.Apply(wanted, updateAccent: false);
                return;
            }

            if (current == applied && current != ApplicationTheme.HighContrast)
            {
                Log.Debug("App theme {Theme} reported again, nothing to do", current);
                return;
            }

            applied = current;
            Log.Information("App theme changed to {Theme} (Windows reports {SystemTheme})", current, ApplicationThemeManager.GetSystemTheme());
            BrandTheme.Apply(current);
        };
        ApplicationThemeManager.Apply(applied, updateAccent: false);
        BrandTheme.Apply(applied);
    }

    private static ApplicationTheme ThemeFor(SystemTheme system) => system switch
    {
        SystemTheme.HC1 or SystemTheme.HC2 or SystemTheme.HCBlack or SystemTheme.HCWhite => ApplicationTheme.HighContrast,
        _ => ApplicationTheme.Dark,
    };

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

    /// <summary>Set when the user asked for a restart; <see cref="Program"/> starts the new process once this one let go.</summary>
    internal static bool RestartRequested { get; private set; }

    private readonly UiExceptionTracker _uiExceptions = new(TimeProvider.System);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
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
            _ = Dispatcher.InvokeAsync(() => AskAboutRepeatedErrorAsync(e.Exception.Message));
            return;
        }

        // Only once the tray exists: resolving it here during a failed startup could throw again.
        _tray?.ShowUnexpectedError(e.Exception.Message);
    }

    private async Task AskAboutRepeatedErrorAsync(string message)
    {
        try
        {
            while (true)
            {
                RepeatedErrorChoice choice = await ProfileDialogs.AskAboutRepeatedErrorAsync(message);
                if (choice == RepeatedErrorChoice.OpenLog)
                {
                    ShellFolders.Open(_paths.Logs, Log.Logger);
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
            // The dialog is WPF too and may be what is broken. The plain message box is not.
            Log.Error(ex, "The dialog about the repeated error failed");
            if (MessageBox.Show($"{message}\n\n{Localization.Loc.Instance["Crash_Restart"]}?", "RigShift", MessageBoxButton.YesNo, MessageBoxImage.Error)
                == MessageBoxResult.Yes)
            {
                RestartRequested = true;
                Quit();
                return;
            }
        }

        _uiExceptions.Reset();
    }
}
