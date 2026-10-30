using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Ipc;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.UI;

public partial class App : Application, IAppShell
{
    private readonly CliRequest _request;
    private readonly AppPaths _paths;
    private ServiceProvider? _services;
    private MainWindow? _window;

    public App(CliRequest request, AppPaths paths)
    {
        _request = request;
        _paths = paths;
        InitializeComponent();
    }

    public bool IsExiting { get; private set; }

    private IServiceProvider Services => _services ?? throw new InvalidOperationException("Services not built.");

    public void ShowMainWindow(AppPage? page = null)
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) => Quit();
        }

        _window.Show(page);
    }

    public void IdentifyDisplays(IReadOnlyList<(int Number, string Name, DisplayAssignment Mode)> displays) =>
        // shortcut: no identify windows yet; they come with the overview page (#91).
        Log.Information("Identifying {Count} displays is not available in this build yet", displays.Count);

    public void Quit()
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        Log.Information("RigShift exits");
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
            Services.GetRequiredService<CommandPipeServer>().Start();
            Services.GetRequiredService<AutomationService>().Start();
            Services.GetRequiredService<GameSessionService>().StartWatching();
            Services.GetRequiredService<UpdateService>().Start();

            if (!_request.Minimized)
            {
                ShowMainWindow();
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RigShift failed to start");
            Quit();
        }
    }

    private ServiceCollection Register(ServiceCollection services)
    {
        DispatcherQueue ui = DispatcherQueue.GetForCurrentThread();
        services.AddRigShiftServices(_paths, this, Log.Logger);
        services.AddSingleton<ISwitchConfirmation, KeepSwitchConfirmation>();
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
