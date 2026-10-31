using System.IO;
using System.Security;
using Avalonia;
using Avalonia.Controls;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Cli;
using RigShift.Core.Ipc;
using RigShift.Core.Settings;
using RigShift.Windows.Shell;
using RigShift.Windows.Startup;
using Serilog;
using Serilog.Core;
using Velopack;

namespace RigShift.Avalonia;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppPaths paths = AppPaths.ForCurrentUser();

        // The log comes first, so Velopack's hooks and update steps are recorded too.
        Log.Logger = AppLogging.Create(paths);
        CrashLogging.Install(Log.Logger);
        try
        {
            return Run(args, paths);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RigShift ended with an exception in Main");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int Run(string[] args, AppPaths paths)
    {
        // Same order as the WPF app: Velopack may exit the process, and only the window app may restart for an update.
        bool isCommand = args.Length > 0 && !args[0].StartsWith('-');
        AppSettings settings = LoadSettings(paths);
        VelopackApp.Build()
            .SetLogger(new SerilogVelopackLogger(Log.Logger))
            .SetAutoApplyOnStartup(!isCommand && settings.OnlyNotifyAboutUpdates is false)
            .OnAfterInstallFastCallback(_ => RegisterUriScheme())
            .OnAfterUpdateFastCallback(_ => RegisterUriScheme())
            .OnBeforeUninstallFastCallback(_ => BeforeUninstall())
            .Run();

        if (args.Length > 0 && RigShiftUri.IsUri(args[0]))
        {
            if (RigShiftUri.ToArguments(args[0]) is not { } linkArguments)
            {
                Log.Warning(
                    "Ignored invalid link {Link}; only rigshift://apply/<profile name>, rigshift://toggle and rigshift://play/<game name> are supported",
                    args[0]);
                CommandLineClient.ReportInvalidLink(args[0]);
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
            return CommandLineClient.Run(args, request, paths);
        }

        using var instance = new Mutex(initiallyOwned: true, PipeProtocol.SingleInstanceMutex, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Started again while RigShift runs: bring the running window forward instead.
            return CommandLineClient.ShowRunningInstance();
        }

        Loc.Instance.SetLanguage(settings.Language);
        int exitCode = AppBuilder.Configure(() => new App(request, paths))
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        instance.ReleaseMutex();
        return exitCode;
    }

    /// <summary>Velopack hook: the installed executable handles rigshift:// links.</summary>
    private static void RegisterUriScheme()
    {
        if (Environment.ProcessPath is { } executable)
        {
            UriSchemeRegistration.Register(executable);
        }
    }

    /// <summary>Velopack hook: removes link handler, autostart entry and own shortcuts. None of it may break the uninstall.</summary>
    private static void BeforeUninstall()
    {
        if (!UriSchemeRegistration.Unregister())
        {
            Log.Warning("Link handler could not be removed before uninstall");
        }

        try
        {
            RunKeyAutostart.Disable(Log.Logger);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            Log.Warning(ex, "Autostart entry could not be removed before uninstall");
        }

        if (Environment.ProcessPath is { } executable)
        {
            IReadOnlyList<string> deleted = ShortcutWriter.DeleteOwn(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), executable);
            Log.Information("Removed {Count} desktop shortcut(s) before uninstall", deleted.Count);
        }
    }

    /// <summary>Runs before Velopack; an unreadable file means defaults, and the app logs that later.</summary>
    private static AppSettings LoadSettings(AppPaths paths) =>
        new JsonSettingsStore(paths.SettingsFile, Logger.None)
            .LoadAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
}
