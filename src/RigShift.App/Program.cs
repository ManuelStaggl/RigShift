using RigShift.App.Services;
using RigShift.Core.Cli;
using RigShift.Core.Settings;
using Serilog;
using Serilog.Core;
using RigShift.Windows.Shell;
using Velopack;

namespace RigShift.App;

public static class Program
{
    /// <summary>One tray app per Windows session.</summary>
    public const string SingleInstanceMutex = @"Local\RigShift.SingleInstance";

    [STAThread]
    public static int Main(string[] args)
    {
        // The log comes first, so Velopack's hooks and update steps are recorded too (analysis finding F-03).
        Log.Logger = AppLogging.Create(App.Paths);
        try
        {
            return Run(args);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int Run(string[] args)
    {
        // Velopack must run first: it handles install/update/uninstall hooks and may exit the process.
        // A pending update restarts the process to install itself. Only the tray app may do that: a CLI call would lose
        // its exit code. Commands are verbs; tray and Velopack hook arguments start with a dash. With automatic
        // installation off, only an explicit "install now" installs (Velopack's updater, see UpdateService).
        // A rigshift:// link is a command too.
        bool isCommand = args.Length > 0 && !args[0].StartsWith('-');
        VelopackApp.Build()
            .SetLogger(new SerilogVelopackLogger(Log.Logger))
            .SetAutoApplyOnStartup(!isCommand && InstallUpdatesAutomatically())
            .OnAfterInstallFastCallback(_ => RegisterUriScheme())
            .OnAfterUpdateFastCallback(_ => RegisterUriScheme())
            .OnBeforeUninstallFastCallback(_ => UriSchemeRegistration.Unregister())
            .Run();

        if (args.Length > 0 && RigShiftUri.IsUri(args[0]))
        {
            if (RigShiftUri.ToArguments(args[0]) is not { } linkArguments)
            {
                Log.Warning("Ignored invalid link {Link}; only rigshift://apply/<profile name> is supported", args[0]);
                return CliExitCodes.InvalidArguments;
            }

            args = [.. linkArguments];
        }

        CliParseResult parsed = CliParser.Parse(args);
        if (parsed.Request is not { } request)
        {
            ParentConsole.Write(parsed.Output);
            return parsed.ExitCode;
        }

        if (request.Command != CliCommand.None)
        {
            return CommandLineClient.Run(args, request);
        }

        using var instance = new Mutex(initiallyOwned: true, SingleInstanceMutex, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Started again while RigShift runs in the tray: bring the running window forward instead.
            return CommandLineClient.ShowRunningInstance();
        }

        var app = new App(request);
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>Velopack hook: the installed executable handles rigshift:// links.</summary>
    private static void RegisterUriScheme()
    {
        if (Environment.ProcessPath is { } executable)
        {
            UriSchemeRegistration.Register(executable);
        }
    }

    /// <summary>Runs before logging is set up; an unreadable file means defaults, and the app logs that later.</summary>
    private static bool InstallUpdatesAutomatically() =>
        new JsonSettingsStore(App.Paths.SettingsFile, Logger.None)
            .LoadAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .OnlyNotifyAboutUpdates is false;
}
