using System.Globalization;
using System.Text;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.Core.Cli;

/// <summary>Runs a switch for the command line. The app implements it with its switch coordinator.</summary>
public interface IProfileSwitcher
{
    /// <returns>The result, or <c>null</c> if another switch is already running.</returns>
    Task<SwitchResult?> SwitchAsync(Profile profile, SwitchRequest request, CancellationToken cancellationToken);
}

public sealed record CliResponse(int ExitCode, string Output);

/// <summary>
/// Executes <see cref="CliRequest"/>s. Runs inside the tray app (all commands) and headless in a short-lived process
/// (<c>list</c>, <c>status</c> – no switcher). Output is English on purpose: scripts parse it.
/// </summary>
public sealed class CommandRunner
{
    private readonly IProfileStore _store;
    private readonly IDisplayConfigurator _display;
    private readonly IAudioController _audio;
    private readonly ActiveProfileMatcher _matcher;
    private readonly IProfileSwitcher? _switcher;
    private readonly ILogger _log;

    public CommandRunner(
        IProfileStore store,
        IDisplayConfigurator display,
        IAudioController audio,
        ActiveProfileMatcher matcher,
        ILogger log,
        IProfileSwitcher? switcher = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _display = display;
        _audio = audio;
        _matcher = matcher;
        _switcher = switcher;
        _log = log.ForContext<CommandRunner>();
    }

    /// <summary>Raised after <c>save</c> wrote a profile, so the app can reload its list.</summary>
    public event EventHandler? ProfilesChanged;

    public async Task<CliResponse> RunAsync(CliRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _log.Information("CLI command {Command} {Profile}", request.Command, request.ProfileName);

        try
        {
            return request.Command switch
            {
                CliCommand.List => await ListAsync(cancellationToken),
                CliCommand.Status => await StatusAsync(cancellationToken),
                CliCommand.Apply => await ApplyAsync(request, cancellationToken),
                CliCommand.Save => await SaveAsync(request.ProfileName ?? string.Empty, cancellationToken),
                _ => new CliResponse(CliExitCodes.InvalidArguments, "No command given."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "CLI command {Command} failed", request.Command);
            return new CliResponse(CliExitCodes.Failed, $"Error: {ex.Message}");
        }
    }

    private async Task<CliResponse> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Profile> profiles = await _store.LoadAllAsync(cancellationToken);
        if (profiles.Count == 0)
        {
            return new CliResponse(CliExitCodes.Applied, "No profiles.");
        }

        Profile? active = null;
        try
        {
            active = _matcher.FindActive(profiles, await _display.QueryAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without display access (e.g. an SSH session) the profiles are still worth listing, just unmarked.
            _log.Warning(ex, "Display configuration unavailable, listing profiles without the active marker");
        }

        return new CliResponse(CliExitCodes.Applied, string.Join(Environment.NewLine,
            profiles.Select(p => (p.Id == active?.Id ? "* " : "  ") + p.Name)));
    }

    private async Task<CliResponse> StatusAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Profile> profiles = await _store.LoadAllAsync(cancellationToken);
        DisplaySnapshot snapshot = await _display.QueryAsync(cancellationToken);
        Profile? active = _matcher.FindActive(profiles, snapshot);

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Active profile: {active?.Name ?? "none"}");
        text.Append("Active displays:");
        foreach (DisplayAssignment display in ProfileEditing.CurrentArrangement(snapshot, [], DisplayNames.Known(profiles)))
        {
            text.AppendLine().Append("  ").Append(Describe(display));
        }

        return new CliResponse(CliExitCodes.Applied, text.ToString());
    }

    private async Task<CliResponse> ApplyAsync(CliRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<Profile> profiles = await _store.LoadAllAsync(cancellationToken);
        if (ProfileEditing.FindByName(profiles, request.ProfileName ?? string.Empty) is not { } profile)
        {
            return NotFound(request.ProfileName, profiles);
        }

        if (_switcher is null)
        {
            return new CliResponse(CliExitCodes.Failed, "Switching needs the RigShift app, which is not running.");
        }

        var switchRequest = new SwitchRequest { DryRun = request.DryRun, SkipConfirmation = request.NoConfirm };
        if (await _switcher.SwitchAsync(profile, switchRequest, cancellationToken) is not { } result)
        {
            return new CliResponse(CliExitCodes.Failed, "Another switch is running. Try again when it has finished.");
        }

        return new CliResponse(CliExitCodes.For(result), DescribeResult(profile, result));
    }

    private async Task<CliResponse> SaveAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new CliResponse(CliExitCodes.InvalidArguments, "A profile name is required.");
        }

        IReadOnlyList<Profile> profiles = await _store.LoadAllAsync(cancellationToken);
        DisplaySnapshot snapshot = await _display.QueryAsync(cancellationToken);
        AudioEndpoint? playback = await DefaultPlaybackAsync(cancellationToken);

        Profile? existing = ProfileEditing.FindByName(profiles, name);
        Profile profile = existing is null
            ? ProfileEditing.Capture(name, snapshot, new AudioAssignment { Playback = playback }, DisplayNames.Known(profiles))
            : existing with
            {
                Displays = ProfileEditing.CurrentArrangement(snapshot, existing.Displays, DisplayNames.Known(profiles)),
                Audio = playback is null ? existing.Audio : existing.Audio with { Playback = playback },
            };

        IReadOnlyList<ProfileProblem> problems = ProfileEditing.Validate(profile, profiles);
        if (problems.Count > 0)
        {
            return new CliResponse(CliExitCodes.Failed, $"Profile not saved: {string.Join(", ", problems)}.");
        }

        await _store.SaveAsync(profile, cancellationToken);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);

        string verb = existing is null ? "Saved" : "Updated";
        return new CliResponse(CliExitCodes.Applied, string.Create(CultureInfo.InvariantCulture,
            $"{verb} profile '{profile.Name}' with {profile.Displays.Count} display(s)."));
    }

    private async Task<AudioEndpoint?> DefaultPlaybackAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AudioDeviceInfo> devices = await _audio.ListAsync(AudioDirection.Render, cancellationToken);
        return devices.FirstOrDefault(d => d.IsDefault && d.IsActive)?.Endpoint;
    }

    private static CliResponse NotFound(string? name, IReadOnlyList<Profile> profiles)
    {
        string available = profiles.Count == 0 ? "none" : string.Join(", ", profiles.Select(p => p.Name));
        return new CliResponse(CliExitCodes.ProfileNotFound, $"Profile '{name}' not found. Available: {available}.");
    }

    private static string DescribeResult(Profile profile, SwitchResult result)
    {
        var text = new StringBuilder();
        if (result.Outcome == SwitchOutcome.DryRun)
        {
            text.Append(CultureInfo.InvariantCulture, $"{profile.Name}: {(result.Plan.IsBlocked ? "blocked" : "ready")}");
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{profile.Name}: {result.Outcome} after {result.Attempts} attempt(s), {result.Duration.TotalSeconds:0.0} s");
            if (result.Audio != AudioOutcome.NotConfigured)
            {
                text.Append(CultureInfo.InvariantCulture, $", audio {result.Audio}");
            }

            if (result.Apps != AppsOutcome.NotConfigured)
            {
                text.Append(CultureInfo.InvariantCulture, $", apps {result.Apps}");
            }
        }

        foreach (MissingDisplay missing in result.Plan.Missing)
        {
            text.AppendLine().Append(CultureInfo.InvariantCulture,
                $"  missing{(missing.Assignment.IsOptional ? " (optional)" : string.Empty)}: {NameOf(missing.Assignment)} ({missing.Reason})");
        }

        foreach (PlanWarning warning in result.Plan.Warnings)
        {
            text.AppendLine().Append("  warning: ").Append(warning.Message);
        }

        if (result.Message is { Length: > 0 } message)
        {
            text.AppendLine().Append("  ").Append(message);
        }

        return text.ToString();
    }

    private static string Describe(DisplayAssignment display)
    {
        double hertz = display.RefreshDenominator == 0 ? 0 : (double)display.RefreshNumerator / display.RefreshDenominator;
        return string.Create(CultureInfo.InvariantCulture,
            $"{NameOf(display)}: {display.Width}x{display.Height} @ {hertz:0.##} Hz at ({display.PositionX}, {display.PositionY}){(display.IsPrimary ? ", primary" : string.Empty)}");
    }

    private static string NameOf(DisplayAssignment display) => DisplayNames.Label(display.CustomName, display.Identity, "unnamed display");
}
