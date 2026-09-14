using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using RigShift.Core.Cli;
using RigShift.Core.Ipc;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Listens on <c>\\.\pipe\RigShift.&lt;SessionId&gt;</c> (current user only) for command lines of further <c>RigShift.exe</c> processes.
/// Each connection is served on its own, so <c>status</c> still answers while an <c>apply</c> waits for confirmation.
/// Commands run on the UI thread, like clicks in the window.
/// </summary>
public sealed class CommandPipeServer : IDisposable
{
    private const int MaxConnections = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>A client that connects but sends no complete request must not hold one of the few instances (analysis finding H-04).</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _requestTimeout;

    private readonly CommandRunner _runner;
    private readonly IAppShell _shell;
    private readonly ILogger _log;
    private readonly string _pipeName;
    private readonly Func<Func<Task<PipeResponse>>, Task<PipeResponse>> _onUiThread;
    private readonly CancellationTokenSource _stop = new();

    public CommandPipeServer(CommandRunner runner, IAppShell shell, ILogger log)
        : this(runner, shell, log, PipeProtocol.PipeName, work => Application.Current.Dispatcher.InvokeAsync(work).Task.Unwrap())
    {
    }

    /// <summary>Tests: an own pipe name, no WPF dispatcher and optionally a shorter request timeout.</summary>
    internal CommandPipeServer(
        CommandRunner runner,
        IAppShell shell,
        ILogger log,
        string pipeName,
        Func<Func<Task<PipeResponse>>, Task<PipeResponse>> onUiThread,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _runner = runner;
        _shell = shell;
        _log = log.ForContext<CommandPipeServer>();
        _pipeName = pipeName;
        _onUiThread = onUiThread;
        _requestTimeout = requestTimeout ?? RequestTimeout;
    }

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
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaxConnections,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // All instances busy, or the name is owned by another user or program (access denied):
                // log and wait instead of spinning or letting the listener die.
                _log.Warning(ex, "Command pipe {Pipe} could not be created, retrying in {Delay}", _pipeName, RetryDelay);
                try
                {
                    await Task.Delay(RetryDelay, cancellationToken);
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
                await server.DisposeAsync();
                if (ex is IOException)
                {
                    _log.Warning(ex, "Command pipe connection failed");
                }

                continue;
            }

            _ = ServeAsync(server, cancellationToken);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await using (server)
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

                PipeResponse response = await _onUiThread(() => ExecuteAsync(request.Arguments));
                await PipeProtocol.WriteResponseAsync(server, response, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException)
            {
                _log.Warning(ex, "Command pipe request could not be served");
            }
        }
    }

    private async Task<PipeResponse> ExecuteAsync(IReadOnlyList<string> args)
    {
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
        return new PipeResponse(response.ExitCode, response.Output);
    }
}
