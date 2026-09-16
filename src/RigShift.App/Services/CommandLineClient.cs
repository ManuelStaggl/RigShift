using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using RigShift.Core.Cli;
using RigShift.Core.Ipc;
using RigShift.Core.Storage;
using RigShift.Core.Topology;
using RigShift.Windows.Audio;
using RigShift.Windows.Display;
using RigShift.Windows.Shell;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// The short-lived <c>RigShift.exe &lt;command&gt;</c> process. It forwards the command to the tray app over the pipe
/// and exits with the app's exit code; the read-only commands are answered locally when no app runs.
/// </summary>
internal static class CommandLineClient
{
    /// <summary>A freshly started app first loads profiles and queries the displays.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private static ILogger Logger => Log.ForContext(typeof(CommandLineClient));

    public static int Run(IReadOnlyList<string> args, CliRequest request) =>
        Task.Run(() => RunAsync(args, request)).GetAwaiter().GetResult();

    public static int ShowRunningInstance()
    {
        // The user just started us, so we may hand the foreground right to the running instance.
        Foreground.AllowAnyProcess();
        return Task.Run(async () =>
        {
            PipeResponse? response = await SendAsync(PipeProtocol.PipeName, [], ConnectTimeout);
            return response?.ExitCode ?? CliExitCodes.Failed;
        }).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(IReadOnlyList<string> args, CliRequest request)
    {
        try
        {
            string? pipe = FindRunningInstance();
            if (pipe is null)
            {
                if (request.Command is CliCommand.List or CliCommand.Status or CliCommand.Surround or CliCommand.Games)
                {
                    return Print(await RunHeadlessAsync(request));
                }

                if (!StartTrayApp())
                {
                    return Print(new CliResponse(CliExitCodes.Failed, "RigShift could not be started. See the log for details."));
                }

                pipe = PipeProtocol.PipeName;
            }

            Foreground.AllowAnyProcess(); // The confirmation window must be able to take the focus.
            PipeResponse? response = await SendAsync(pipe, args, ConnectTimeout);
            return response is null
                ? Print(new CliResponse(CliExitCodes.Failed, "RigShift did not respond. See the log for details."))
                : Print(new CliResponse(response.ExitCode, response.Output));
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static int Print(CliResponse response)
    {
        ParentConsole.Write(response.Output);
        return response.ExitCode;
    }

    /// <summary>
    /// The pipe of a running RigShift, our own Windows session first. Looking for the pipe rather than for the
    /// single-instance mutex matters because the mutex is session-local: a command sent over SSH, from a scheduled
    /// task or from a service runs in a session without a desktop, where the display API refuses everything. Handing
    /// it to the instance that does sit on the desktop is the only way such a command can be answered at all.
    /// Only this user's instance will accept us; the pipe's access list on the server side sees to that.
    /// </summary>
    private static string? FindRunningInstance()
    {
        string own = PipeProtocol.PipeName;
        List<string> found;
        try
        {
            found = [.. Directory.GetFiles(PipeDirectory)
                .Select(Path.GetFileName)
                .Where(name => name is not null && name.StartsWith(PipePrefix, StringComparison.Ordinal))
                .Select(name => name!)
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "The list of named pipes could not be read; falling back to this session");
            return Mutex.TryOpenExisting(Program.SingleInstanceMutex, out Mutex? mutex) ? Keep(mutex, own) : null;
        }

        if (found.Contains(own, StringComparer.Ordinal))
        {
            return own;
        }

        if (found.Count == 0)
        {
            return null;
        }

        // Another session holds the only instance - the usual case for a command that arrives without a desktop.
        Logger.Information("No RigShift in this session; forwarding to {Pipe}", found[0]);
        return found[0];
    }

    private static string Keep(Mutex mutex, string pipe)
    {
        mutex.Dispose();
        return pipe;
    }

    private const string PipeDirectory = @"\\.\pipe\";
    private const string PipePrefix = "RigShift.";

    private static async Task<CliResponse> RunHeadlessAsync(CliRequest request)
    {
        ILogger log = Log.Logger;
        var runner = new CommandRunner(
            new JsonProfileStore(App.Paths.Profiles, log),
            new CcdDisplayConfigurator(log, TimeProvider.System),
            new PolicyConfigAudioController(log),
            new ActiveProfileMatcher(new TopologyPlanner(new TopologyPlannerOptions())),
            log,
            switcher: null,
            new NvSurroundController(new CcdDisplayConfigurator(log, TimeProvider.System), log),
            new JsonGameStore(App.Paths.DataDirectory, log, TimeProvider.System),
            player: null);
        return await runner.RunAsync(request, CancellationToken.None);
    }

    private static bool StartTrayApp()
    {
        string? executable = Environment.ProcessPath;
        if (executable is null)
        {
            Logger.Error("Own executable path is unknown, cannot start the tray app");
            return false;
        }

        try
        {
            // Shell execute: the long-lived tray app must not inherit our stdout, or a caller reading it to the end
            // (a pipe, a script) would wait until the tray app exits.
            using Process? process = Process.Start(new ProcessStartInfo(executable, "--minimized") { UseShellExecute = true });
            Logger.Information("Started tray app {Executable} for a command line request", executable);
            return process is not null;
        }
        catch (Win32Exception ex)
        {
            Logger.Error(ex, "Tray app {Executable} could not be started", executable);
            return false;
        }
    }

    private static async Task<PipeResponse?> SendAsync(string pipeName, IReadOnlyList<string> args, TimeSpan connectTimeout)
    {
        try
        {
            // No CurrentUserOnly: it compares token owners, which differ between an elevated and a plain process of
            // the same user. Who may connect is decided by the pipe's access list on the server side.
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using (var connect = new CancellationTokenSource(connectTimeout))
            {
                await pipe.ConnectAsync(connect.Token);
            }

            await PipeProtocol.WriteRequestAsync(pipe, new PipeRequest(args), CancellationToken.None);

            // No timeout: a switch waits for sleeping monitors and for the user to confirm.
            return await PipeProtocol.ReadResponseAsync(pipe, CancellationToken.None);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Logger.Error(ex, "Command pipe request {Arguments} failed", args);
            return null;
        }
    }
}
