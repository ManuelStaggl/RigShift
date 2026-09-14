using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.Core.Api;

/// <summary>
/// Endpoints of the local HTTP API (docs/PLAN.md, section 6, item 11). Same scope as the command line:
/// <c>GET /api/status</c>, <c>GET /api/profiles</c>, <c>POST /api/profiles/{name}/apply[?dryRun&amp;noConfirm]</c>.
/// Every call needs <c>Authorization: Bearer &lt;token&gt;</c>; a switch answers once it has finished.
/// </summary>
public sealed class ApiHandler
{
    private readonly IProfileStore _store;
    private readonly IDisplayConfigurator _display;
    private readonly ActiveProfileMatcher _matcher;
    private readonly IProfileSwitcher _switcher;
    private readonly Func<string?> _token;
    private readonly ILogger _log;

    /// <param name="token">Current token; read per request so a regenerated token applies at once.</param>
    public ApiHandler(
        IProfileStore store,
        IDisplayConfigurator display,
        ActiveProfileMatcher matcher,
        IProfileSwitcher switcher,
        Func<string?> token,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(switcher);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _display = display;
        _matcher = matcher;
        _switcher = switcher;
        _token = token;
        _log = log.ForContext<ApiHandler>();
    }

    public async Task<ApiResponse> HandleAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Defense in depth against DNS rebinding; the token alone already stops foreign web pages.
        if (!IsLoopbackHost(request.Header("Host")))
        {
            return Error(403, "Only requests to 127.0.0.1 or localhost are accepted.");
        }

        if (!IsAuthorized(request.Header("Authorization")))
        {
            return Error(401, "Missing or wrong token. Send 'Authorization: Bearer <token>' (see RigShift settings).");
        }

        string[] segments = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            return segments switch
            {
                ["api", "status"] => request.Method == "GET" ? await StatusAsync(cancellationToken) : MethodNotAllowed("GET"),
                ["api", "profiles"] => request.Method == "GET" ? await ProfilesAsync(cancellationToken) : MethodNotAllowed("GET"),
                ["api", "profiles", var name, "apply"] => request.Method == "POST"
                    ? await ApplyAsync(Uri.UnescapeDataString(name), request, cancellationToken)
                    : MethodNotAllowed("POST"),
                _ => Error(404, "Unknown endpoint. Available: GET /api/status, GET /api/profiles, POST /api/profiles/{name}/apply."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "API request {Method} {Path} failed", request.Method, request.Path);
            return Error(500, ex.Message);
        }
    }

    /// <summary>A new random token: 32 bytes as lowercase hex.</summary>
    public static string CreateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private async Task<ApiResponse> StatusAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Profile> profiles = await _store.LoadAllAsync(cancellationToken);
        DisplaySnapshot snapshot;
        try
        {
            snapshot = await _display.QueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Display configuration unavailable for the API status");
            return Error(503, "Display configuration unavailable.");
        }

        Profile? active = _matcher.FindActive(profiles, snapshot);
        List<ApiDisplay> displays = ProfileEditing.CurrentArrangement(snapshot, [], DisplayNames.Known(profiles))
            .Select(d => new ApiDisplay(
                DisplayNames.Label(d.CustomName, d.Identity, "unnamed display"),
                d.Width,
                d.Height,
                d.RefreshDenominator == 0 ? 0 : Math.Round((double)d.RefreshNumerator / d.RefreshDenominator, 2),
                d.PositionX,
                d.PositionY,
                d.IsPrimary))
            .ToList();

        return Ok(new ApiStatus(active is null ? null : new ApiProfile(active.Id, active.Name, IsActive: true), displays), ApiJsonContext.Readable.ApiStatus);
    }

    private async Task<ApiResponse> ProfilesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Profile> profiles = await _store.LoadAllAsync(cancellationToken);
        Profile? active = null;
        try
        {
            active = _matcher.FindActive(profiles, await _display.QueryAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Display configuration unavailable, listing profiles without the active marker");
        }

        List<ApiProfile> items = profiles.Select(p => new ApiProfile(p.Id, p.Name, p.Id == active?.Id)).ToList();
        return Ok(items, ApiJsonContext.Readable.ListApiProfile);
    }

    private async Task<ApiResponse> ApplyAsync(string name, ApiRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<Profile> profiles = await _store.LoadAllAsync(cancellationToken);
        Profile? profile = Guid.TryParse(name, out Guid id)
            ? profiles.FirstOrDefault(p => p.Id == id)
            : ProfileEditing.FindByName(profiles, name);
        if (profile is null)
        {
            string available = profiles.Count == 0 ? "none" : string.Join(", ", profiles.Select(p => p.Name));
            return Error(404, $"Profile '{name}' not found. Available: {available}.");
        }

        var switchRequest = new SwitchRequest { DryRun = request.Flag("dryRun"), SkipConfirmation = request.Flag("noConfirm") };
        _log.Information("API switch to {Profile} (dry run {DryRun}, no confirm {NoConfirm})", profile.Name, switchRequest.DryRun, switchRequest.SkipConfirmation);
        if (await _switcher.SwitchAsync(profile, switchRequest, cancellationToken) is not { } result)
        {
            return Error(409, "Another switch is running. Try again when it has finished.");
        }

        var body = new ApiSwitchResult(
            profile.Name,
            result.Outcome,
            CliExitCodes.For(result),
            result.Attempts,
            Math.Round(result.Duration.TotalSeconds, 1),
            result.Audio,
            result.Apps,
            result.Plan.Missing.Select(m => new ApiMissingDisplay(
                DisplayNames.Label(m.Assignment.CustomName, m.Assignment.Identity, "unnamed display"), m.Assignment.IsOptional, m.Reason)).ToList(),
            result.Plan.Warnings.Select(w => w.Message).ToList(),
            result.Message);
        return Ok(body, ApiJsonContext.Readable.ApiSwitchResult);
    }

    private bool IsAuthorized(string? authorization)
    {
        const string Scheme = "Bearer ";
        if (_token() is not { Length: > 0 } expected
            || authorization is null
            || !authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(authorization[Scheme.Length..].Trim()), Encoding.UTF8.GetBytes(expected));
    }

    private static bool IsLoopbackHost(string? host)
    {
        if (host is null)
        {
            return true; // HTTP/1.0 clients may omit it.
        }

        int colon = host.LastIndexOf(':');
        string name = colon > 0 ? host[..colon] : host;
        return name.Equals("127.0.0.1", StringComparison.Ordinal) || name.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static ApiResponse Ok<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        new(200, JsonSerializer.Serialize(value, type));

    private static ApiResponse MethodNotAllowed(string allowed) => Error(405, $"Use {allowed} for this endpoint.");

    internal static ApiResponse Error(int statusCode, string message) =>
        new(statusCode, JsonSerializer.Serialize(new ApiError(message), ApiJsonContext.Readable.ApiError));
}

public sealed record ApiProfile(Guid Id, string Name, bool IsActive);

public sealed record ApiDisplay(string Name, int Width, int Height, double RefreshHz, int X, int Y, bool IsPrimary);

public sealed record ApiStatus(ApiProfile? ActiveProfile, IReadOnlyList<ApiDisplay> Displays);

public sealed record ApiMissingDisplay(string Name, bool IsOptional, MissingReason Reason);

public sealed record ApiSwitchResult(
    string Profile,
    SwitchOutcome Outcome,
    int ExitCode,
    int Attempts,
    double DurationSeconds,
    AudioOutcome Audio,
    AppsOutcome Apps,
    IReadOnlyList<ApiMissingDisplay> Missing,
    IReadOnlyList<string> Warnings,
    string? Message);

public sealed record ApiError(string Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ApiStatus))]
[JsonSerializable(typeof(List<ApiProfile>))]
[JsonSerializable(typeof(ApiSwitchResult))]
[JsonSerializable(typeof(ApiError))]
internal sealed partial class ApiJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Keeps apostrophes and umlauts in names readable instead of <c>\uXXXX</c> escapes. Safe here: the body is only ever
    /// served as <c>application/json</c>, never embedded in HTML.
    /// </summary>
    public static ApiJsonContext Readable { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
