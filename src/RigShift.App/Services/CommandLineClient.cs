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
/// and exits with the app's exit code; <c>list</c> and <c>status</c> are answered locally when no app runs
///.
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
            PipeResponse? response = await SendAsync([], ConnectTimeout);
            return response?.ExitCode ?? CliExitCodes.Failed;
        }).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(IReadOnlyList<string> args, CliRequest request)
    {
        try
        {
            bool running = IsAppRunning();
            if (!running && request.Command is CliCommand.List or CliCommand.Status)
            {
                return Print(await RunHeadlessAsync(request));
            }

            if (!running && !StartTrayApp())
            {
                return Print(new CliResponse(CliExitCodes.Failed, "RigShift could not be started. See the log for details."));
            }

            Foreground.AllowAnyProcess(); // The confirmation window must be able to take the focus.
            PipeResponse? response = await SendAsync(args, ConnectTimeout);
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

    private static bool IsAppRunning()
    {
        if (Mutex.TryOpenExisting(Program.SingleInstanceMutex, out Mutex? mutex))
        {
            mutex.Dispose();
            return true;
        }

        return false;
    }

    private static async Task<CliResponse> RunHeadlessAsync(CliRequest request)
    {
        ILogger log = Log.Logger;
        var runner = new CommandRunner(
            new JsonProfileStore(App.Paths.Profiles, log),
            new CcdDisplayConfigurator(log, TimeProvider.System),
            new PolicyConfigAudioController(log),
            new ActiveProfileMatcher(new TopologyPlanner(new TopologyPlannerOptions())),
            log);
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

    private static async Task<PipeResponse?> SendAsync(IReadOnlyList<string> args, TimeSpan connectTimeout)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
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
