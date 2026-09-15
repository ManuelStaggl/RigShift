using System.Windows.Interop;
using RigShift.App.Localization;
using RigShift.App.Views;
using RigShift.Core.Profiles;
using RigShift.Windows.Ui;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Registers the profile hotkeys and the "back to the previous profile" hotkey with Windows and switches when one is
/// pressed – the same way as a tray click, including the confirmation countdown. Pressing the hotkey again during that
/// countdown confirms (docs/PLAN.md, section 6).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int ProbeId = 0xBFFF;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>Key of the toggle hotkey in <see cref="_wanted"/>; no profile has this id.</summary>
    private static readonly Guid ToggleKey = Guid.Empty;

    private readonly ProfileCatalog _catalog;
    private readonly SwitchCoordinator _coordinator;
    private readonly SettingsService _settings;
    private readonly ILogger _log;
    private readonly HwndSource _source;

    /// <summary>Registration id → profile, or <see cref="ToggleKey"/>.</summary>
    private readonly Dictionary<int, Guid> _registered = [];
    private Dictionary<Guid, Hotkey> _wanted = [];
    private bool _suspended;
    private bool _started;

    public HotkeyService(ProfileCatalog catalog, SwitchCoordinator coordinator, SettingsService settings, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _catalog = catalog;
        _coordinator = coordinator;
        _settings = settings;
        _log = log.ForContext<HotkeyService>();

        // Message-only window: WM_HOTKEY is posted to the registering window, no broadcast needed.
        _source = new HwndSource(new HwndSourceParameters("RigShift.Hotkeys") { ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// Raised once at startup with the names of profiles whose hotkey another application holds; the toggle hotkey is
    /// listed under its settings label.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? RegistrationFailed;

    public void Start()
    {
        _started = true;
        _catalog.Changed += (_, _) => Sync(reportFailures: false);
        _settings.Changed += (_, _) => Sync(reportFailures: false);
        Sync(reportFailures: true);
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
        Sync(reportFailures: false, force: true);
    }

    /// <summary>
    /// Whether Windows would accept <paramref name="hotkey"/> right now. Only meaningful while suspended; otherwise
    /// RigShift's own registrations count as taken.
    /// </summary>
    public bool IsAvailable(Hotkey hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        if (!NativeWindow.RegisterHotkey(_source.Handle, ProbeId, (int)hotkey.Modifiers, hotkey.VirtualKey, out int error))
        {
            _log.Information("Hotkey {Modifiers}+0x{Key:X2} is not available, error {Error}", hotkey.Modifiers, hotkey.VirtualKey, error);
            return false;
        }

        NativeWindow.UnregisterHotkey(_source.Handle, ProbeId);
        return true;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private void Sync(bool reportFailures, bool force = false)
    {
        if (!_started || _suspended)
        {
            return;
        }

        Dictionary<Guid, Hotkey> wanted = _catalog.Profiles
            .Where(p => p.Hotkey is { IsValid: true })
            .ToDictionary(p => p.Id, p => p.Hotkey!);
        if (_settings.Current.ToggleHotkey is { IsValid: true } toggle)
        {
            wanted[ToggleKey] = toggle;
        }

        // The catalog also changes whenever the active profile is refreshed; only re-register on real changes.
        if (!force && wanted.Count == _wanted.Count && wanted.All(w => _wanted.TryGetValue(w.Key, out Hotkey? old) && old == w.Value))
        {
            return;
        }

        UnregisterAll();
        _wanted = wanted;

        var failed = new List<string>();
        int id = 1;
        IEnumerable<(Guid Key, string Name)> owners = _catalog.Profiles
            .Where(p => wanted.ContainsKey(p.Id))
            .Select(p => (p.Id, p.Name))
            .Concat(wanted.ContainsKey(ToggleKey) ? [(ToggleKey, Loc.Instance["Settings_ToggleHotkey"])] : []);
        foreach ((Guid key, string name) in owners)
        {
            Hotkey hotkey = wanted[key];
            if (NativeWindow.RegisterHotkey(_source.Handle, id, (int)hotkey.Modifiers, hotkey.VirtualKey, out int error))
            {
                _registered[id] = key;
                _log.Information("Hotkey {Modifiers}+0x{Key:X2} registered for {Owner}", hotkey.Modifiers, hotkey.VirtualKey, name);
            }
            else
            {
                failed.Add(name);
                _log.Warning("Hotkey {Modifiers}+0x{Key:X2} for {Owner} could not be registered, error {Error} ({Reason})",
                    hotkey.Modifiers, hotkey.VirtualKey, name, error,
                    error == ErrorHotkeyAlreadyRegistered ? "taken by another application" : "unexpected");
            }

            id++;
        }

        if (reportFailures && failed.Count > 0)
        {
            RegistrationFailed?.Invoke(this, failed);
        }
    }

    private void UnregisterAll()
    {
        foreach (int id in _registered.Keys)
        {
            NativeWindow.UnregisterHotkey(_source.Handle, id);
        }

        _registered.Clear();
        _wanted = [];
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeWindow.WmHotkey && _registered.TryGetValue((int)wParam, out Guid profileId))
        {
            handled = true;
            OnPressed(profileId);
        }

        return 0;
    }

    private void OnPressed(Guid key)
    {
        Profile? profile = key == ToggleKey ? _coordinator.ToggleTarget : _catalog.Find(key);
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
}
