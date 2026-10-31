using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Avalonia.Services;
using RigShift.Avalonia.Tray;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Cli;
using RigShift.Core.Profiles;
using RigShift.Core.Settings;
using RigShift.Core.Topology;
using RigShift.Windows.Ui;
using Serilog;

namespace RigShift.Avalonia;

/// <summary>The tray app: startup and exit, the services, the tray and the window with rail and pages.</summary>
public sealed class App(CliRequest request, AppPaths paths) : Application, IAppShell
{
    private ServiceProvider? _services;
    private MainWindow? _window;
    private TrayIconService? _tray;
    private Profile? _lastActive;

    internal static IServiceProvider Provider => ((App)Current!)._services ?? throw new InvalidOperationException("Services not built.");

    public bool IsExiting { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        // Screenshots in a fixed theme whatever Windows uses: RIGSHIFT_PREVIEW_THEME=Light|Dark.
        RequestedThemeVariant = Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_THEME") switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => RequestedThemeVariant,
        };
#endif
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        Directory.CreateDirectory(paths.DataDirectory);
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        HighContrast.Follow(this);
        LogFileHeader.Enabled = true; // Heads the next line: version, Windows, graphics driver.
        Log.Information("RigShift {Version} (Avalonia) starting, data directory {DataDirectory}", typeof(App).Assembly.GetName().Version, paths.DataDirectory);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested += OnShutdownRequested;
            desktop.Exit += OnExit;
        }

        try
        {
            _services = Register(new ServiceCollection()).BuildServiceProvider();
            await Provider.GetRequiredService<SettingsService>().LoadAsync(CancellationToken.None);
            ProfileCatalog catalog = Provider.GetRequiredService<ProfileCatalog>();
            await catalog.ReloadAsync(CancellationToken.None);
            LogFileHeader.AppState = () => DescribeState(catalog);
            Log.Information("{AppState}", DescribeState(catalog));

            // Games are loaded before the page is opened: the command line plays them, the watcher for games started
            // elsewhere needs them, and a game that already runs must only set the starting point, not trigger a switch.
            await Provider.GetRequiredService<GameCatalog>().ReloadAsync(CancellationToken.None);
            await catalog.BackupOnceForVersionAsync(typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown", CancellationToken.None);

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
            var quickSwitch = new QuickSwitchViewModel(Provider.GetRequiredService<TrayPopupViewModel>());
            HotkeyService hotkeys = Provider.GetRequiredService<HotkeyService>();
            hotkeys.QuickSwitchRequested += (_, _) => Dispatcher.UIThread.Post(() => Tray.QuickSwitchWindow.Open(quickSwitch));
            hotkeys.Start();

            Provider.GetRequiredService<DisplayChangeWatcher>().DisplaysChanged +=
                async (_, _) =>
                {
                    // Monitors in standby or a return over RDP pass through "no profile": the same profile coming back
                    // is not one Windows restored on its own.
                    Profile? before = catalog.ActiveProfile ?? _lastActive;
                    SwitchCoordinator coordinator = Provider.GetRequiredService<SwitchCoordinator>();
                    await catalog.RefreshActiveAsync(CancellationToken.None);
                    await coordinator.CatchUpAsync();
                    coordinator.NoticeDisplayChange(before, catalog.ActiveProfile);
                    _lastActive = catalog.ActiveProfile ?? before;
                };

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

            KeepAwakeForActiveProfile(catalog);
            UpgradeAutostartEntry();
            await ApplySignInProfileAsync(catalog);
            ReportSettingsProblem();
            await OfferInterruptedRestoreAsync();
            await Provider.GetRequiredService<SwitchOrchestrator>().RestoreDuckingIfUnusedAsync(catalog.ActiveProfile, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RigShift failed to start");
            NativeWindow.ShowError(Loc.Format("Error_Startup", UserMessages.Describe(ex), paths.Logs));
            Quit();
        }
    }

    // Entries from before 4.2 lack --autostart, so the next sign-in would not count (issue #13).
    private static void UpgradeAutostartEntry()
    {
        try
        {
            Provider.GetRequiredService<SettingsService>().Autostart.UpgradeEntry();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Warning(ex, "Autostart entry could not be upgraded");
        }
    }

    // Windows restores the layout the PC shut down in, rig included; a USB rule with connected devices wins.
    private async Task ApplySignInProfileAsync(ProfileCatalog catalog)
    {
        if (!request.StartedWithWindows)
        {
            return;
        }

        AppSettings settings = Provider.GetRequiredService<SettingsService>().Current;
        if (settings.SignInProfileId is null)
        {
            return;
        }

        IUsbDeviceList devices = Provider.GetRequiredService<IUsbDeviceList>();
        IReadOnlySet<string> present;
        try
        {
            present = await Task.Run(devices.PresentDeviceIds);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "USB devices could not be read at startup, sign-in profile not applied");
            return;
        }

        (StartupProfileDecision decision, Profile? profile) = StartupProfile.Choose(settings, catalog.Profiles, catalog.ActiveProfile?.Id, present);
        Log.Information("Started with Windows, sign-in profile {Profile}: {Decision}", profile?.Name ?? "(none)", decision);
        if (decision == StartupProfileDecision.Apply && profile is not null)
        {
            await Provider.GetRequiredService<SwitchCoordinator>().SwitchAsync(profile);
        }
    }

    private static string DescribeState(ProfileCatalog catalog) =>
        catalog.LastSnapshot is { } snapshot
            ? FormattableString.Invariant($"{snapshot.Displays.Count(d => d.IsActive)} of {snapshot.Displays.Count} displays active · {catalog.Profiles.Count} profiles · active profile: {catalog.ActiveProfile?.Name ?? "none"}")
            : FormattableString.Invariant($"Displays not read yet · {catalog.Profiles.Count} profiles");

    // An unfinished recorded switch is the one case the in-process rollback cannot cover; posted so startup finishes
    // first.
    private static async Task OfferInterruptedRestoreAsync()
    {
        ISwitchJournal journal = Provider.GetRequiredService<ISwitchJournal>();
        if (await journal.ReadAsync(CancellationToken.None) is not { } interrupted)
        {
            return;
        }

        Log.Warning("Found an unfinished switch to {Profile} started {Started:u}", interrupted.TargetProfileName, interrupted.StartedUtc);
        Dispatcher.UIThread.Post(async () =>
        {
            if (await PageDialogs.ConfirmRestoreInterruptedAsync(interrupted.TargetProfileName))
            {
                // Without asking again: the user just answered, and a second countdown on top would only confuse.
                await Provider.GetRequiredService<SwitchCoordinator>()
                    .SwitchAsync(interrupted.Previous, new SwitchRequest { SkipConfirmation = true });
            }
            else
            {
                await journal.ClearAsync(CancellationToken.None);
            }
        });
    }

    // Posted so startup finishes before the dialog blocks.
    private void ReportSettingsProblem()
    {
        SettingsLoadReport report = Provider.GetRequiredService<SettingsService>().LastLoad;
        if (report.Problem == SettingsLoadProblem.None)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            if (await PageDialogs.ShowSettingsProblemAsync(report))
            {
                ShellFolders.Open(paths.DataDirectory, Log.Logger);
            }
        });
    }

    // The keep-awake request ends with the process, so a restart renews it for the active profile.
    private static void KeepAwakeForActiveProfile(ProfileCatalog catalog)
    {
        if (catalog.ActiveProfile is not { KeepAwake: true } active)
        {
            return;
        }

        try
        {
            Log.Information("Active profile {Profile} keeps the PC awake", active.Name);
            Provider.GetRequiredService<IPowerController>().SetKeepAwake(true);
        }
        catch (Win32Exception ex)
        {
            Log.Warning(ex, "Keep-awake for {Profile} could not be set at startup", active.Name);
        }
    }

    // Blocking here would deadlock the rollback (the countdown needs this thread), so the session end is refused once.
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (IsExiting)
        {
            return;
        }

        if (_services?.GetService<SwitchCoordinator>() is { IsSwitching: true })
        {
            Log.Warning("Windows session ending during a switch, refusing until it has rolled back");
            e.Cancel = true;
            if (_services.GetService<DisplayChangeWatcher>() is { } watcher)
            {
                NativeWindow.ExplainShutdownBlock(watcher.Handle, Loc.Instance["Session_SwitchRunning"]);
            }

            Quit();
            return;
        }

        // Ending now: the window must not go to the tray instead of closing.
        Log.Information("Windows session ending, RigShift exits");
        IsExiting = true;
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Log.Information("RigShift exiting with code {ExitCode}", e.ApplicationExitCode);
        // Disposes the singletons in reverse creation order. The log stays open: Program.Main closes it.
        _services?.Dispose();
    }

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
        if (_window.WindowState == global::Avalonia.Controls.WindowState.Minimized)
        {
            _window.WindowState = global::Avalonia.Controls.WindowState.Normal;
        }

        _window.Activate();
        if (page is { } shown)
        {
            _window.Navigate(shown);
        }
    }

    internal MainWindow? Window => _window;

    internal static Window? Owner => ((App)Current!)._window is { IsVisible: true } window ? window : null;

    internal static nint OwnerHandle => Owner?.TryGetPlatformHandle()?.Handle ?? 0;

    public void IdentifyDisplays(IReadOnlyList<(int Number, string Name, DisplayAssignment Mode)> displays) =>
        Views.IdentifyWindow.ShowAll(displays);

    /// <summary>
    /// Ends the app. A running switch is cancelled first and may roll back for up to 30 s, so displays, audio and the
    /// ducking setting are never left half-switched.
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
            Log.Information("RigShift exits");
            _window?.Close();
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
    }

    // Windows 11 hides new tray icons behind the "^", so the first close says where RigShift went.
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

    public void QuitByUser() => _ = QuitByUserAsync();

    private async Task QuitByUserAsync()
    {
        try
        {
            var profiles = Provider.GetRequiredService<ProfilesViewModel>();
            var games = Provider.GetRequiredService<GamesViewModel>();
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

            // Without RigShift nothing switches back when the game ends.
            GameCatalog catalog = Provider.GetRequiredService<GameCatalog>();
            string running = string.Join(", ", Provider.GetRequiredService<GameSessionService>().RunningGames
                .Select(id => catalog.Find(id)?.Name).OfType<string>());
            if (running.Length > 0)
            {
                Log.Information("Exit requested while {Games} runs, asking first", running);
                if (!await PageDialogs.ConfirmQuitDuringGameAsync(running))
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

    private ServiceCollection Register(ServiceCollection services)
    {
        services.AddRigShiftApp(paths, this, Log.Logger);
        AppIcons.Extract = ProgramIcons.Extract;
#if DEBUG
        Debug.Previews.Register(services);
#endif
        return services;
    }
}
