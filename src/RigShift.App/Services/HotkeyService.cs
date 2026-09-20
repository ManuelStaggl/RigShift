using RigShift.App.Localization;
using RigShift.App.Views;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Registers the profile hotkeys, the game hotkeys and the "back to the previous profile" hotkey with Windows, and
/// switches or starts a game when one is pressed – the same way as a tray click, including the confirmation countdown.
/// Pressing a profile's hotkey again during that countdown confirms.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int ProbeId = 0xBFFF;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>Key of the toggle hotkey in <see cref="_wanted"/>; no profile has this id.</summary>
    private static readonly HotkeyOwner ToggleKey = new(Guid.Empty, IsGame: false);

    private readonly ProfileCatalog _catalog;
    private readonly GameCatalog _games;
    private readonly GameSessionService _sessions;
    private readonly SwitchCoordinator _coordinator;
    private readonly SettingsService _settings;
    private readonly ILogger _log;
    private readonly IHotkeyRegistrar _registrar;

    /// <summary>Registration id → what it belongs to.</summary>
    private readonly Dictionary<int, HotkeyOwner> _registered = [];
    private Dictionary<HotkeyOwner, Hotkey> _wanted = [];

    /// <summary>Failures the user has been told about; the same one is not announced again on every sync.</summary>
    private HashSet<(HotkeyOwner Owner, Hotkey Hotkey)> _reported = [];
    private bool _suspended;
    private bool _started;

    public HotkeyService(
        ProfileCatalog catalog,
        GameCatalog games,
        GameSessionService sessions,
        SwitchCoordinator coordinator,
        SettingsService settings,
        ILogger log)
        : this(catalog, games, sessions, coordinator, settings, log, registrar: null)
    {
    }

    internal HotkeyService(
        ProfileCatalog catalog,
        GameCatalog games,
        GameSessionService sessions,
        SwitchCoordinator coordinator,
        SettingsService settings,
        ILogger log,
        IHotkeyRegistrar? registrar)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _catalog = catalog;
        _games = games;
        _sessions = sessions;
        _coordinator = coordinator;
        _settings = settings;
        _log = log.ForContext<HotkeyService>();
        _registrar = registrar ?? new Win32HotkeyRegistrar();
        _registrar.Pressed += OnRegistrarPressed;
    }

    /// <summary>
    /// Raised with the names of profiles and games whose hotkey does not work – another application holds it, or
    /// something else in RigShift does. At startup and whenever a new one turns up later; the toggle hotkey is listed
    /// under its settings label.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? RegistrationFailed;

    public void Start()
    {
        _started = true;
        _catalog.Changed += (_, _) => Sync();
        _games.Changed += (_, _) => Sync();
        _settings.Changed += (_, _) => Sync();
        Sync();
    }

    /// <summary>Releases all hotkeys, e.g. while the profile editor records a new one.</summary>
    public void Suspend()
    {
        _suspended = true;
        UnregisterAll();
        _log.Information("Hotkeys suspended");
    }

    public void Resume()
    {
        _suspended = false;
        _log.Information("Hotkeys resumed");
        Sync(force: true);
    }

    /// <summary>
    /// Whether Windows would accept <paramref name="hotkey"/> right now. Only meaningful while suspended; otherwise
    /// RigShift's own registrations count as taken.
    /// </summary>
    public bool IsAvailable(Hotkey hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        if (!_registrar.Register(ProbeId, hotkey, out int error))
        {
            _log.Information("Hotkey {Modifiers}+0x{Key:X2} is not available, error {Error}", hotkey.Modifiers, hotkey.VirtualKey, error);
            return false;
        }

        _registrar.Unregister(ProbeId);
        return true;
    }

    /// <summary>
    /// What else in RigShift already uses <paramref name="hotkey"/> – a profile, a game or the toggle hotkey – or
    /// <c>null</c>. <see cref="IsAvailable"/> cannot see these: the own hotkeys are released while one is recorded.
    /// </summary>
    public HotkeyUse? UsedBy(Hotkey hotkey, HotkeyUseKind kind, Guid id)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        return HotkeyConflicts.Find(hotkey, new HotkeyUse(kind, id, null), _catalog.Profiles, _games.Games, _settings.Current.ToggleHotkey);
    }

    /// <summary>The hint for a combination <see cref="UsedBy"/> found taken.</summary>
    public static string UsedByText(HotkeyUse use)
    {
        ArgumentNullException.ThrowIfNull(use);
        return Loc.Format("Problem_HotkeyUsedBy", use.Name ?? Loc.Instance["Settings_ToggleHotkey"]);
    }

    public void Dispose()
    {
        UnregisterAll();
        _registrar.Pressed -= OnRegistrarPressed;
        _registrar.Dispose();
    }

    private void Sync(bool force = false)
    {
        if (!_started || _suspended)
        {
            return;
        }

        // Windows registers a combination once. When two things in RigShift carry the same one – a file edited by hand,
        // a restored backup – the first keeps it and the other is reported, instead of failing as "another application".
        var wanted = new Dictionary<HotkeyOwner, Hotkey>();
        var names = new Dictionary<HotkeyOwner, string>();
        var holders = new Dictionary<Hotkey, string>();
        var failed = new List<(HotkeyOwner Owner, Hotkey Hotkey, string Name)>();
        foreach ((HotkeyUse use, Hotkey hotkey) in HotkeyConflicts.All(_catalog.Profiles, _games.Games, _settings.Current.ToggleHotkey))
        {
            var owner = new HotkeyOwner(use.Id, use.Kind == HotkeyUseKind.Game);
            string name = use.Name ?? Loc.Instance["Settings_ToggleHotkey"];
            wanted[owner] = hotkey;
            names[owner] = name;
        }

        // The catalog also changes whenever the active profile is refreshed; only re-register on real changes.
        if (!force && wanted.Count == _wanted.Count && wanted.All(w => _wanted.TryGetValue(w.Key, out Hotkey? old) && old == w.Value))
        {
            return;
        }

        UnregisterAll();
        _wanted = wanted;

        int id = 1;
        foreach ((HotkeyOwner owner, Hotkey hotkey) in wanted)
        {
            string name = names[owner];
            if (holders.TryGetValue(hotkey, out string? holder))
            {
                failed.Add((owner, hotkey, name));
                _log.Warning("Hotkey {Modifiers}+0x{Key:X2} for {Owner} is not registered: {Holder} already uses it",
                    hotkey.Modifiers, hotkey.VirtualKey, name, holder);
            }
            else if (_registrar.Register(id, hotkey, out int error))
            {
                holders[hotkey] = name;
                _registered[id] = owner;
                _log.Information("Hotkey {Modifiers}+0x{Key:X2} registered for {Owner}", hotkey.Modifiers, hotkey.VirtualKey, name);
            }
            else
            {
                failed.Add((owner, hotkey, name));
                _log.Warning("Hotkey {Modifiers}+0x{Key:X2} for {Owner} could not be registered, error {Error} ({Reason})",
                    hotkey.Modifiers, hotkey.VirtualKey, name, error,
                    error == ErrorHotkeyAlreadyRegistered ? "taken by another application" : "unexpected");
            }

            id++;
        }

        // A hotkey that stopped working after a change is as broken as one at startup; only the repeat is left out.
        List<string> news = [.. failed.Where(f => !_reported.Contains((f.Owner, f.Hotkey))).Select(f => f.Name)];
        _reported = [.. failed.Select(f => (f.Owner, f.Hotkey))];
        if (news.Count > 0)
        {
            RegistrationFailed?.Invoke(this, news);
        }
    }

    private void UnregisterAll()
    {
        foreach (int id in _registered.Keys)
        {
            _registrar.Unregister(id);
        }

        _registered.Clear();
        _wanted = [];
    }

    private void OnRegistrarPressed(object? sender, int id)
    {
        if (_registered.TryGetValue(id, out HotkeyOwner owner))
        {
            OnPressed(owner);
        }
    }

    private void OnPressed(HotkeyOwner key)
    {
        if (key.IsGame)
        {
            StartGame(key.Id);
            return;
        }

        Profile? profile = key == ToggleKey ? _coordinator.ToggleTarget : _catalog.Find(key.Id);
        if (profile is null)
        {
            if (key == ToggleKey)
            {
                _log.Information("Toggle hotkey pressed, but there is no previous profile to go back to");
            }

            return;
        }

        if (_coordinator.IsSwitching && ConfirmationWindow.TryConfirm(profile.Id))
        {
            _log.Information("Hotkey for {Profile} pressed again, switch confirmed", profile.Name);
            return;
        }

        _log.Information("{Source} pressed, switching to {Profile}", key == ToggleKey ? "Toggle hotkey" : "Hotkey", profile.Name);
        _ = _coordinator.SwitchAsync(profile);
    }

    /// <summary>
    /// A game hotkey runs the whole session. Pressing it again while it runs does nothing – unlike a profile hotkey,
    /// where the second press confirms the countdown; here the countdown belongs to the session's own switch.
    /// </summary>
    private void StartGame(Guid gameId)
    {
        if (_games.Find(gameId) is not { } game)
        {
            _log.Information("Game hotkey pressed for a game that no longer exists");
            return;
        }

        _log.Information("Game hotkey pressed, starting {Game}", game.Name);
        _sessions.Start(game);
    }
}

/// <summary>
/// What a registration belongs to. A game and a profile could in principle carry the same id, and their hotkeys do
/// entirely different things, so the flag is part of the key rather than a lookup in two catalogs.
/// </summary>
internal readonly record struct HotkeyOwner(Guid Id, bool IsGame);
