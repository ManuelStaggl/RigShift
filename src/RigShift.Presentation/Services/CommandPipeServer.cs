using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using RigShift.App.Localization;
using RigShift.Core.Cli;
using RigShift.Core.Ipc;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Listens on <c>\\.\pipe\RigShift.&lt;SessionId&gt;</c> for command lines of further <c>RigShift.exe</c> processes.
/// Only this user may connect, enforced by the pipe's own access list - see <see cref="OnlyThisUser"/> - and the
/// server never joins a pipe of this name that somebody else put up first, see <see cref="CreateInstance"/>.
/// Each connection is served on its own, so <c>status</c> still answers while an <c>apply</c> waits for confirmation.
/// Commands run on the UI thread, like clicks in the window.
/// </summary>
public sealed class CommandPipeServer : IDisposable
{
    private const int MaxConnections = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    // A client that connects but sends nothing complete must not hold one of the few instances.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _retryDelay;

    private readonly CommandRunner _runner;
    private readonly IAppShell _shell;
    private readonly ILogger _log;
    private readonly string _pipeName;
    private readonly Func<Func<Task<PipeResponse>>, Task<PipeResponse>> _onUiThread;
    private readonly CancellationTokenSource _stop = new();

    // Guards _instances together with creating and closing instances.
    private readonly Lock _gate = new();
    private int _instances;

    /// <param name="onUiThread">Runs a request on the app's UI thread.</param>
    public CommandPipeServer(CommandRunner runner, IAppShell shell, ILogger log, Func<Func<Task<PipeResponse>>, Task<PipeResponse>> onUiThread)
        : this(runner, shell, log, PipeProtocol.PipeName, onUiThread)
    {
    }

    /// <summary>Tests: an own pipe name and optionally shorter waits.</summary>
    internal CommandPipeServer(
        CommandRunner runner,
        IAppShell shell,
        ILogger log,
        string pipeName,
        Func<Func<Task<PipeResponse>>, Task<PipeResponse>> onUiThread,
        TimeSpan? requestTimeout = null,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _runner = runner;
        _shell = shell;
        _log = log.ForContext<CommandPipeServer>();
        _pipeName = pipeName;
        _onUiThread = onUiThread;
        _requestTimeout = requestTimeout ?? RequestTimeout;
        _retryDelay = retryDelay ?? RetryDelay;
    }

    /// <summary>
    /// A request the caller cannot show the answer of: a link, a Stream Deck key or a desktop shortcut has no console.
    ///The text is for the tray, in the app's language.
    /// </summary>
    public event EventHandler<string>? Refused;

    public void Start()
    {
        _ = Task.Run(() => ListenAsync(_stop.Token)).ContinueWith(
            task => _log.Error(task.Exception, "Command pipe {Pipe} listener stopped unexpectedly", _pipeName),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _log.Information("Command pipe {Pipe} listening", _pipeName);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        bool createFailed = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateInstance();
                createFailed = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // All instances busy, or the name is owned by another user or program (access denied): wait instead
                // of spinning or letting the listener die. Said once, not every two seconds.
                if (!createFailed)
                {
                    _log.Warning(ex, "Command pipe {Pipe} could not be created, retrying every {Delay}", _pipeName, _retryDelay);
                    createFailed = true;
                }

                try
                {
                    await Task.Delay(_retryDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                await CloseAsync(server);
                if (ex is IOException)
                {
                    _log.Warning(ex, "Command pipe connection failed");
                }

                continue;
            }

            _ = ServeAsync(server, cancellationToken);
        }
    }

    // While none of ours exists the name must be free, or we could join a foreign pipe whose access list decides; the
    // lock spans the creation.
    private NamedPipeServerStream CreateInstance()
    {
        lock (_gate)
        {
            PipeOptions options = PipeOptions.Asynchronous | (_instances == 0 ? PipeOptions.FirstPipeInstance : PipeOptions.None);
            NamedPipeServerStream server = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, MaxConnections,
                PipeTransmissionMode.Byte, options, 0, 0, OnlyThisUser());
            _instances++;
            return server;
        }
    }

    private async ValueTask CloseAsync(NamedPipeServerStream server)
    {
        await server.DisposeAsync();
        lock (_gate)
        {
            _instances--;
        }
    }

    // Keyed on the user SID: CurrentUserOnly compares the token owner, which is BUILTIN\Administrators for an elevated
    // SSH session.
    private static PipeSecurity OnlyThisUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
        var security = new PipeSecurity();

        // Nothing else is granted, so the list starts and ends here: read, write, and the right to put up the next
        // instance of the same pipe name for the following client.
        security.AddAccessRule(new PipeAccessRule(
            user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return security;
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                PipeRequest request;
                using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    readTimeout.CancelAfter(_requestTimeout);
                    try
                    {
                        request = await PipeProtocol.ReadRequestAsync(server, readTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _log.Warning("Command pipe client sent no complete request within {Timeout}, connection closed", _requestTimeout);
                        return;
                    }
                }

                if (!IsThisUser(server))
                {
                    return;
                }

                PipeResponse response = await _onUiThread(() => ExecuteAsync(request.Arguments));
                await PipeProtocol.WriteResponseAsync(server, response, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException)
            {
                _log.Warning(ex, "Command pipe request could not be served");
            }
        }
        finally
        {
            await CloseAsync(server);
        }
    }

    // A second check behind the access list; when Windows will not name the client, the access list decides.
    private bool IsThisUser(NamedPipeServerStream server)
    {
        try
        {
            using WindowsIdentity own = WindowsIdentity.GetCurrent();
            SecurityIdentifier? client = PipeTrust.ClientUser(server);
            if (client is null || own.User is null || client.Equals(own.User))
            {
                return true;
            }

            _log.Error("Command pipe request from another user ({Client}) refused", client.Value);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            _log.Warning(ex, "The command pipe client could not be identified; its access list decides");
            return true;
        }
    }

    private async Task<PipeResponse> ExecuteAsync(IReadOnlyList<string> args)
    {
        // A valid rigshift:// link arrives as a command; a raw one is a link the caller could not read.
        if (args.Count == 1 && RigShiftUri.IsUri(args[0]))
        {
            string link = args[0].Length > 200 ? args[0][..200] + "…" : args[0];
            _log.Warning("Invalid link {Link} reported by its caller", link);
            Refused?.Invoke(this, Loc.Format("Tray_LinkInvalid", link));
            return new PipeResponse(CliExitCodes.InvalidArguments, "Invalid link.");
        }

        CliParseResult parsed = CliParser.Parse(args);
        if (parsed.Request is not { } request)
        {
            return new PipeResponse(parsed.ExitCode, parsed.Output);
        }

        if (request.Command == CliCommand.None)
        {
            _shell.ShowMainWindow();
            return new PipeResponse(CliExitCodes.Applied, string.Empty);
        }

        CliResponse response = await _runner.RunAsync(request, CancellationToken.None);
        if (RefusalText(request, response.ExitCode) is { } text)
        {
            Refused?.Invoke(this, text);
        }

        return new PipeResponse(response.ExitCode, response.Output);
    }

    // Usually a profile or game renamed after its shortcut or Stream Deck key was made; a blocked or rolled-back switch
    // told the user already.
    private static string? RefusalText(CliRequest request, int exitCode) => (request.Command, exitCode) switch
    {
        (CliCommand.Apply, CliExitCodes.ProfileNotFound) => Loc.Format("Tray_ProfileNotFound", request.ProfileName),
        (CliCommand.Play, CliExitCodes.ProfileNotFound) => Loc.Format("Tray_GameNotFound", request.GameName),
        (CliCommand.Toggle, CliExitCodes.ProfileNotFound) => Loc.Instance["Tray_NoPreviousProfile"],
        _ => null,
    };
}
