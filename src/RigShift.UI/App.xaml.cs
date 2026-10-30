using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Ipc;
using RigShift.Core.Profiles;
using RigShift.UI.Services;
using RigShift.UI.Tray;
using Serilog;

namespace RigShift.UI;

public partial class App : Application, IAppShell
{
    private readonly CliRequest _request;
    private readonly AppPaths _paths;
    private ServiceProvider? _services;
    private MainWindow? _window;
    private TrayIconService? _tray;

    public App(CliRequest request, AppPaths paths)
    {
        _request = request;
        _paths = paths;
        InitializeComponent();
    }

    public bool IsExiting { get; private set; }

    /// <summary>The app's services, for pages the frame creates by type.</summary>
    internal static IServiceProvider Provider => ((App)Current).Services;

    private IServiceProvider Services => _services ?? throw new InvalidOperationException("Services not built.");

    public void ShowMainWindow(AppPage? page = null)
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.AppWindow.Closing += (window, e) =>
            {
                // Closing the window keeps RigShift in the tray; Exit in the tray menu ends it.
                if (!IsExiting)
                {
                    e.Cancel = true;
                    window.Hide();
                    OnHiddenToTray();
                }
            };
        }

        _window.Show(page);
    }

    public void IdentifyDisplays(IReadOnlyList<(int Number, string Name, DisplayAssignment Mode)> displays) => IdentifyWindow.ShowAll(displays);

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
        Exit();
    }

    // shortcut: nothing to ask about until the editors exist (#92, #93); then this asks like the WPF app.
    public void QuitByUser() => Quit();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        UnhandledException += (_, e) => Log.Error(e.Exception, "Unhandled exception on the UI thread");
        LogFileHeader.Enabled = true; // Heads the next line: version, Windows, graphics driver.
        Log.Information("RigShift {Version} (WinUI) starting, data directory {DataDirectory}", typeof(App).Assembly.GetName().Version, _paths.DataDirectory);

        try
        {
            _services = Register(new ServiceCollection()).BuildServiceProvider();
            await Services.GetRequiredService<SettingsService>().LoadAsync(CancellationToken.None);
            ProfileCatalog catalog = Services.GetRequiredService<ProfileCatalog>();
            await catalog.ReloadAsync(CancellationToken.None);
            await Services.GetRequiredService<GameCatalog>().ReloadAsync(CancellationToken.None);

            // The pipe first: a second start or a rigshift:// link waits for it.
            CommandRunner runner = Services.GetRequiredService<CommandRunner>();
            runner.ProfilesChanged += async (_, _) => await catalog.ReloadAsync(CancellationToken.None);
            CommandPipeServer pipe = Services.GetRequiredService<CommandPipeServer>();
            pipe.Refused += (_, text) => _tray?.ShowRefusedCommand(text);
            pipe.Start();

            _tray = Services.GetRequiredService<TrayIconService>();
            _tray.Start();
            Services.GetRequiredService<AutomationService>().Start();
            Services.GetRequiredService<GameSessionService>().StartWatching();
            Services.GetRequiredService<UpdateService>().Start();

            if (!_request.Minimized)
            {
                ShowMainWindow();
            }
#if DEBUG
            // Screenshots of the popup without clicking the tray: it opens next to wherever the cursor is. After the
            // window, which would take the focus and so close it.
            if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_TRAY") == "1")
            {
                _tray.ShowPopup();
            }

            if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_TOPOLOGY") == "1")
            {
                new Debug.TopologyGalleryWindow(catalog.Items.Select(i => (i.Name, i.Topology))).Activate();
            }
#endif
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RigShift failed to start");
            Quit();
        }
    }

    /// <summary>
    /// The first close of the window says where RigShift went: Windows 11 puts a new tray icon behind the "^", and a
    /// window that simply vanishes looks like a closed app. Once, remembered in the settings.
    /// </summary>
    private async void OnHiddenToTray()
    {
        SettingsService settings = Services.GetRequiredService<SettingsService>();
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

    private ServiceCollection Register(ServiceCollection services)
    {
        DispatcherQueue ui = DispatcherQueue.GetForCurrentThread();
        services.AddRigShiftServices(_paths, this, Log.Logger);
        services.AddSingleton<ISwitchConfirmation, KeepSwitchConfirmation>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<ITextClipboard, WinUiClipboard>();
        services.AddSingleton<PendingPageDialogs>();
        services.AddSingleton<IProfilePageDialogs>(sp => sp.GetRequiredService<PendingPageDialogs>());
        services.AddSingleton<IHelpPageDialogs>(sp => sp.GetRequiredService<PendingPageDialogs>());
        AppIcons.Extract = WinUiIcons.Extract;
        services.AddSingleton(sp => new CommandPipeServer(
            sp.GetRequiredService<CommandRunner>(), this, Log.Logger, work => OnUiThread(ui, work)));
        return services;
    }

    /// <summary>Runs a pipe request on the UI thread; its awaits stay there.</summary>
    private static Task<PipeResponse> OnUiThread(DispatcherQueue ui, Func<Task<PipeResponse>> work)
    {
        var done = new TaskCompletionSource<PipeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = ui.TryEnqueue(async () =>
        {
            try
            {
                done.SetResult(await work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        if (!queued)
        {
            done.SetException(new InvalidOperationException("The UI thread has ended."));
        }

        return done.Task;
    }
}
