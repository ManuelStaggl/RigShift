using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using RigShift.Core.Cli;
using RigShift.Core.Ipc;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Listens on <c>\\.\pipe\RigShift</c> (current user only) for command lines of further <c>RigShift.exe</c> processes.
/// Each connection is served on its own, so <c>status</c> still answers while an <c>apply</c> waits for confirmation.
/// Commands run on the UI thread, like clicks in the window.
/// </summary>
public sealed class CommandPipeServer : IDisposable
{
    private const int MaxConnections = 4;

    private readonly CommandRunner _runner;
    private readonly IAppShell _shell;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();

    public CommandPipeServer(CommandRunner runner, IAppShell shell, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _runner = runner;
        _shell = shell;
        _log = log.ForContext<CommandPipeServer>();
    }

    public void Start()
    {
        _ = Task.Run(() => ListenAsync(_stop.Token));
        _log.Information("Command pipe {Pipe} listening", PipeProtocol.PipeName);
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
                server = new NamedPipeServerStream(PipeProtocol.PipeName, PipeDirection.InOut, MaxConnections,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException ex)
            {
                // All instances busy or the name is taken by something else: wait instead of spinning.
                _log.Warning(ex, "Command pipe could not be created, retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
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
                PipeRequest request = await PipeProtocol.ReadRequestAsync(server, cancellationToken);
                PipeResponse response = await Application.Current.Dispatcher
                    .InvokeAsync(() => ExecuteAsync(request.Arguments))
                    .Task
                    .Unwrap();
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
