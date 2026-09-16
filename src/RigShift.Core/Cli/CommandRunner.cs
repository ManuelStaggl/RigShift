using System.Globalization;
using System.Text;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.Core.Cli;

/// <summary>Runs a switch for the command line. The app implements it with its switch coordinator.</summary>
public interface IProfileSwitcher
{
    /// <summary>Where <c>toggle</c> goes right now (<see cref="ProfileEditing.ToggleTarget"/>), or <c>null</c>.</summary>
    Profile? ToggleTarget { get; }

    /// <returns>The result, or <c>null</c> if another switch is already running.</returns>
    Task<SwitchResult?> SwitchAsync(Profile profile, SwitchRequest request, CancellationToken cancellationToken);
}

/// <summary>Runs game sessions for the command line and the tray menu. The app implements it with its session service.</summary>
public interface IGamePlayer
{
    /// <summary>True while a session for this game is running.</summary>
    bool IsRunning(Guid gameId);

    /// <returns><c>false</c> when a session for this game is already running, so nothing was started a second time.</returns>
    bool Play(GameEntry game);
}

public sealed record CliResponse(int ExitCode, string Output);

/// <summary>
/// Executes <see cref="CliRequest"/>s. Runs inside the tray app (all commands) and headless in a short-lived process
/// (<c>list</c>, <c>status</c>, <c>games</c> – no switcher, no player). Output is English on purpose: scripts parse it.
/// </summary>
public sealed class CommandRunner
{
    private readonly IProfileStore _store;
    private readonly IDisplayConfigurator _display;
    private readonly IAudioController _audio;
    private readonly ActiveProfileMatcher _matcher;
    private readonly IProfileSwitcher? _switcher;
    private readonly ISurroundController? _surround;
    private readonly IGameStore? _games;
    private readonly IGamePlayer? _player;
    private readonly ILogger _log;

    public CommandRunner(
        IProfileStore store,
        IDisplayConfigurator display,
        IAudioController audio,
        ActiveProfileMatcher matcher,
        ILogger log,
        IProfileSwitcher? switcher = null,
        ISurroundController? surround = null,
        IGameStore? games = null,
        IGamePlayer? player = null)
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
        _surround = surround;
        _games = games;
        _player = player;
        _log = log.ForContext<CommandRunner>();
    }

    /// <summary>Raised after <c>save</c> wrote a profile, so the app can reload its list.</summary>
    public event EventHandler? ProfilesChanged;

    public async Task<CliResponse> RunAsync(CliRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _log.Information("CLI command {Command} {Name}", request.Command, request.ProfileName ?? request.GameName);

        try
        {
            return request.Command switch
            {
                CliCommand.List => await ListAsync(cancellationToken),
                CliCommand.Status => await StatusAsync(cancellationToken),
                CliCommand.Surround => await SurroundAsync(cancellationToken),
                CliCommand.Apply => await ApplyAsync(request, cancellationToken),
                CliCommand.Toggle => await ToggleAsync(request, cancellationToken),
                CliCommand.Save => await SaveAsync(request.ProfileName ?? string.Empty, cancellationToken),
                CliCommand.Games => await GamesAsync(cancellationToken),
                CliCommand.Play => await PlayAsync(request.GameName ?? string.Empty, cancellationToken),
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
        IReadOnlyList<Profile> profiles = (await _store.LoadAllAsync(cancellationToken)).Profiles;
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
        IReadOnlyList<Profile> profiles = (await _store.LoadAllAsync(cancellationToken)).Profiles;
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

    /// <summary>
    /// Reads the Surround state and changes nothing. Worth its own command rather than a line in <c>status</c>: scripts
    /// parse that output, and this one only says something on NVIDIA machines.
    /// </summary>
    private async Task<CliResponse> SurroundAsync(CancellationToken cancellationToken)
    {
        if (_surround is null)
        {
            return new CliResponse(CliExitCodes.Failed, "Surround cannot be read in this process.");
        }

        SurroundState state = await _surround.QueryAsync(cancellationToken);
        var text = new StringBuilder();
        text.Append("Surround: ").Append(state.Availability switch
        {
            SurroundAvailability.NoDriver => "no NVIDIA graphics driver",
            SurroundAvailability.Unknown => "state unreadable",
            _ => state.IsActive ? "on" : "off",
        });
        if (state.Message is { } message)
        {
            text.Append(" (").Append(message).Append(')');
        }

        foreach (SurroundGrid grid in state.Grids)
        {
            text.AppendLine().Append(CultureInfo.InvariantCulture,
                $"  Grid {grid.Columns}x{grid.Rows}: {grid.Displays.Count} displays of {grid.Width}x{grid.Height}");
            if (grid.RefreshRateHz > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $" at {grid.RefreshRateHz} Hz");
            }

            text.Append(CultureInfo.InvariantCulture, $" as {grid.TotalWidth}x{grid.TotalHeight}");
        }

        IReadOnlyList<SurroundDisplay> displays = await _surround.ListDisplaysAsync(cancellationToken);
        foreach (SurroundDisplay display in displays)
        {
            text.AppendLine().Append(CultureInfo.InvariantCulture, $"  Display {display.DisplayId:X8} {display.Name ?? "(no name)"}");
        }

        return new CliResponse(CliExitCodes.Applied, text.ToString());
    }

    private async Task<CliResponse> ApplyAsync(CliRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<Profile> profiles = (await _store.LoadAllAsync(cancellationToken)).Profiles;
        if (ProfileEditing.FindByName(profiles, request.ProfileName ?? string.Empty) is not { } profile)
        {
            return NotFound(request.ProfileName, profiles);
        }

        return await SwitchAsync(profile, request, cancellationToken);
    }

    /// <summary><c>toggle</c>: back to the previous profile, or to the default profile when nothing was active before.</summary>
    private async Task<CliResponse> ToggleAsync(CliRequest request, CancellationToken cancellationToken)
    {
        if (_switcher is null)
        {
            return new CliResponse(CliExitCodes.Failed, "Switching needs the RigShift app, which is not running.");
        }

        if (_switcher.ToggleTarget is not { } profile)
        {
            return new CliResponse(CliExitCodes.ProfileNotFound,
                "No previous profile to go back to. Switch to a profile first, or set a default profile in the settings.");
        }

        return await SwitchAsync(profile, request, cancellationToken);
    }

    private async Task<CliResponse> SwitchAsync(Profile profile, CliRequest request, CancellationToken cancellationToken)
    {
        if (_switcher is null)
        {
            return new CliResponse(CliExitCodes.Failed, "Switching needs the RigShift app, which is not running.");
        }

        var switchRequest = new SwitchRequest { DryRun = request.DryRun, SkipConfirmation = request.NoConfirm, FromLink = request.FromLink };
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

        LoadResult loaded = await _store.LoadAllAsync(cancellationToken);
        IReadOnlyList<Profile> profiles = loaded.Profiles;
        Profile? existing = ProfileEditing.FindByName(profiles, name);
        if (existing is null && !loaded.IsComplete)
        {
            // The profile may live in one of the unreadable files; saving now would create a second one with the same name.
            string files = string.Join(", ", loaded.Unreadable.Select(f => f.FileName));
            _log.Warning("Profile {Profile} not saved: not found and {Count} profile file(s) unreadable ({Files})", name, loaded.Unreadable.Count, files);
            return new CliResponse(CliExitCodes.Failed, string.Create(CultureInfo.InvariantCulture,
                $"Profile '{name}' not saved: {loaded.Unreadable.Count} profile file(s) could not be read ({files}). One of them may already contain this profile. Close the program using the file or fix it, then try again."));
        }

        DisplaySnapshot snapshot = await _display.QueryAsync(cancellationToken);
        AudioEndpoint? playback = await DefaultPlaybackAsync(cancellationToken);

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

    /// <summary>
    /// The configured games, a running one marked with <c>*</c> – the same shape as <c>list</c>, so a script can read
    /// both the same way. Without a player (headless call, no app running) nothing can be running, so nothing is marked.
    /// </summary>
    private async Task<CliResponse> GamesAsync(CancellationToken cancellationToken)
    {
        if (_games is null)
        {
            return new CliResponse(CliExitCodes.Failed, "Games cannot be read in this process.");
        }

        GameLoadResult loaded = await _games.LoadAllAsync(cancellationToken);
        if (!loaded.IsComplete)
        {
            // An empty list would be a lie here, exactly as on the games page.
            return new CliResponse(CliExitCodes.Failed, $"Games could not be read: {loaded.Unreadable}");
        }

        return loaded.Games.Count == 0
            ? new CliResponse(CliExitCodes.Applied, "No games.")
            : new CliResponse(CliExitCodes.Applied, string.Join(Environment.NewLine,
                loaded.Games.Select(g => (_player?.IsRunning(g.Id) == true ? "* " : "  ") + g.Name)));
    }

    /// <summary>
    /// Starts a game session and returns at once: the session outlives the command by hours, so waiting for it would
    /// leave a console process hanging around for the whole evening.
    /// </summary>
    private async Task<CliResponse> PlayAsync(string name, CancellationToken cancellationToken)
    {
        if (_games is null || _player is null)
        {
            return new CliResponse(CliExitCodes.Failed, "Starting a game needs the RigShift app, which is not running.");
        }

        GameLoadResult loaded = await _games.LoadAllAsync(cancellationToken);
        if (GameEditing.FindByName(loaded.Games, name) is not { } game)
        {
            string available = loaded.Games.Count == 0 ? "none" : string.Join(", ", loaded.Games.Select(g => g.Name));
            return new CliResponse(CliExitCodes.ProfileNotFound, $"Game '{name}' not found. Available: {available}.");
        }

        return _player.Play(game)
            ? new CliResponse(CliExitCodes.Applied, $"Started '{game.Name}'.")
            : new CliResponse(CliExitCodes.Failed, $"'{game.Name}' is already running.");
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

            if (result.Apps == AppsOutcome.Pending)
            {
                text.Append(", apps start in the background");
            }
            else if (result.Apps != AppsOutcome.NotConfigured)
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

        if (result.Apps == AppsOutcome.DeviceMissing)
        {
            text.AppendLine().Append(CultureInfo.InvariantCulture,
                $"  {profile.AppsWaitForUsbDeviceName ?? profile.AppsWaitForUsbDeviceId} was not detected within {Profile.AppsDeviceWaitSeconds} s, apps started anyway");
        }

        if (result.Message is { Length: > 0 } message)
        {
            text.AppendLine().Append("  ").Append(message);
        }

        string? note = result.Note switch
        {
            SwitchNote.RestoredPrevious when result.Outcome != SwitchOutcome.RolledBack => "previous displays restored",
            SwitchNote.RestoreFailed => "the previous displays could not be restored",
            SwitchNote.ModesFromDatabase => "Windows used its own display modes, the stored ones did not work",
            _ => null,
        };
        if (note is not null)
        {
            text.AppendLine().Append("  note: ").Append(note);
        }

        return text.ToString();
    }

    private static string Describe(DisplayAssignment display)
    {
        double hertz = RefreshRate.Of(display).Hertz;
        return string.Create(CultureInfo.InvariantCulture,
            $"{NameOf(display)}: {display.Width}x{display.Height} @ {hertz:0.##} Hz at ({display.PositionX}, {display.PositionY}){(display.IsPrimary ? ", primary" : string.Empty)}");
    }

    private static string NameOf(DisplayAssignment display) => DisplayNames.Label(display.CustomName, display.Identity, "unnamed display");
}
