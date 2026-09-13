using RigShift.App.Services;
using RigShift.Core.Cli;
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
        // Velopack must run first: it handles install/update/uninstall hooks and may exit the process.
        VelopackApp.Build().Run();

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
}
