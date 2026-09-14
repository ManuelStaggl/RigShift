using System.Net;
using System.Net.Sockets;
using Serilog;

namespace RigShift.Core.Api;

/// <summary>
/// Accepts HTTP connections on 127.0.0.1 only and hands each request to a handler. A <see cref="TcpListener"/> instead
/// of <c>HttpListener</c>: http.sys needs an admin URL reservation for <c>127.0.0.1</c> (docs/PLAN.md, section 6, item 11).
/// </summary>
public sealed class HttpApiServer : IAsyncDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<ApiRequest, CancellationToken, Task<ApiResponse>> _handler;
    private readonly ILogger _log;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <param name="port">TCP port; 0 picks a free one (tests), see <see cref="Port"/>.</param>
    /// <exception cref="SocketException">The port is in use or not allowed.</exception>
    public HttpApiServer(int port, Func<ApiRequest, CancellationToken, Task<ApiResponse>> handler, ILogger log)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(log);

        _handler = handler;
        _log = log.ForContext<HttpApiServer>();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptAsync(_stop.Token));
        _log.Information("HTTP API listening on http://127.0.0.1:{Port}/api/", Port);
    }

    public int Port { get; }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_loop is not null)
        {
            await _loop;
            _loop = null;
        }

        _stop.Dispose();
        _log.Information("HTTP API on port {Port} stopped", Port);
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log.Warning(ex, "HTTP API connection could not be accepted");
                continue;
            }

            _ = ServeAsync(client, cancellationToken);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            NetworkStream stream = client.GetStream();
            ApiRequest? request = null;
            try
            {
                using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    readTimeout.CancelAfter(ReadTimeout);
                    request = await HttpMessages.ReadRequestAsync(stream, readTimeout.Token);
                }

                if (request is null)
                {
                    return;
                }

                // No timeout here: a switch waits for the confirmation countdown.
                ApiResponse response = await _handler(request, cancellationToken);
                _log.Information("HTTP API {Method} {Path} -> {StatusCode}", request.Method, request.Path, response.StatusCode);
                await HttpMessages.WriteResponseAsync(stream, response, cancellationToken);
            }
            catch (InvalidDataException ex)
            {
                _log.Warning(ex, "HTTP API received a malformed request");
                await TryWriteAsync(stream, ApiHandler.Error(400, ex.Message));
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                _log.Warning(ex, "HTTP API request {Method} {Path} could not be served", request?.Method, request?.Path);
            }
        }
    }

    private async Task TryWriteAsync(Stream stream, ApiResponse response)
    {
        try
        {
            await HttpMessages.WriteResponseAsync(stream, response, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            _log.Debug(ex, "HTTP API error response could not be sent");
        }
    }
}
