using System.Net.Sockets;
using System.Windows;
using RigShift.Core.Abstractions;
using RigShift.Core.Api;
using RigShift.Core.Cli;
using RigShift.Core.Settings;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Runs the local HTTP API while it is switched on in the settings and restarts it when the port changes
/// (docs/PLAN.md, section 6, item 11). Requests run on the UI thread, like commands from the pipe.
/// </summary>
public sealed class HttpApiService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ApiHandler _handler;
    private readonly ILogger _log;
    private HttpApiServer? _server;

    public HttpApiService(
        SettingsService settings,
        IProfileStore store,
        IDisplayConfigurator display,
        ActiveProfileMatcher matcher,
        SwitchCoordinator switcher,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        _settings = settings;
        _log = log.ForContext<HttpApiService>();
        _handler = new ApiHandler(store, display, matcher, (IProfileSwitcher)switcher, () => _settings.Current.HttpApiToken, log);
    }

    public event EventHandler? StateChanged;

    /// <summary>Port the API listens on, or <c>null</c> while it is off or could not start.</summary>
    public int? ListeningPort => _server?.Port;

    /// <summary>Why the API could not start, e.g. the port is in use.</summary>
    public string? Error { get; private set; }

    public void Start()
    {
        _settings.Changed += async (_, _) => await ApplyAsync();
        _ = ApplyAsync();
    }

    public void Dispose() => _server?.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task ApplyAsync()
    {
        AppSettings current = _settings.Current;
        int? wanted = current.HttpApiEnabled && !string.IsNullOrEmpty(current.HttpApiToken) ? current.HttpApiPort : null;
        if (wanted == _server?.Port && (wanted is not null || Error is null))
        {
            return;
        }

        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }

        Error = null;
        if (wanted is { } port)
        {
            try
            {
                _server = new HttpApiServer(port, DispatchAsync, _log);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentOutOfRangeException)
            {
                _log.Warning(ex, "HTTP API could not listen on port {Port}", port);
                Error = ex.Message;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private Task<ApiResponse> DispatchAsync(ApiRequest request, CancellationToken cancellationToken) =>
        Application.Current.Dispatcher.InvokeAsync(() => _handler.HandleAsync(request, cancellationToken)).Task.Unwrap();
}
