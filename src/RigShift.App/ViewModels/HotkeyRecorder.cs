using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

/// <summary>What a hotkey field in the window talks to (<see cref="Controls.HotkeyBox"/>).</summary>
public interface IHotkeyField
{
    /// <summary>The field took the focus: RigShift's own hotkeys rest, so pressing one records it instead of firing it.</summary>
    void BeginHotkeyRecording();

    void EndHotkeyRecording();

    /// <summary>A combination was pressed; without Ctrl, Alt or Win it only shows a hint.</summary>
    void RecordHotkey(HotkeyModifiers modifiers, int virtualKey);

    void ClearHotkey();
}

/// <summary>
/// The rules of a hotkey field, the same for the profile editor, the game editor and the settings (v4 finding A-07): a
/// combination needs Ctrl, Alt or Win, must be free in Windows and must not belong to another profile, game or "back".
/// Keeps the hint under the field, in the current language.
/// </summary>
/// <param name="kind">Who asks: the conflict check leaves the asker's own hotkey out.</param>
/// <param name="ownerId">The profile or game being edited; <see cref="Guid.Empty"/> for the toggle hotkey.</param>
/// <param name="defaultHintKey">The hint while nothing went wrong.</param>
public sealed class HotkeyRecorder(HotkeyService hotkeys, HotkeyUseKind kind, Guid ownerId, string defaultHintKey = "Editor_HotkeyHint")
{
    private string? _hintKey;
    private HotkeyUse? _conflict;

    /// <summary>The hint in the current language; a combination taken inside RigShift names who holds it.</summary>
    public string Hint => _conflict is { } use ? HotkeyService.UsedByText(use) : Loc.Instance[_hintKey ?? defaultHintKey];

    public void Begin() => hotkeys.Suspend();

    public void End() => hotkeys.Resume();

    /// <summary>The combination to take, or <c>null</c> when it is refused; <see cref="Hint"/> says why.</summary>
    public Hotkey? Record(HotkeyModifiers modifiers, int virtualKey)
    {
        var hotkey = new Hotkey { Modifiers = modifiers, VirtualKey = virtualKey };
        if (!hotkey.IsValid)
        {
            SetHint("Editor_HotkeyNeedsModifier");
            return null;
        }

        // Hotkeys are suspended while the field has the focus, so this sees only other applications.
        if (!hotkeys.IsAvailable(hotkey))
        {
            SetHint("Problem_HotkeyInUse");
            return null;
        }

        // The own hotkeys are released right now, so Windows cannot tell that a profile, a game or "back" holds this one.
        if (hotkeys.UsedBy(hotkey, kind, ownerId) is { } use)
        {
            _conflict = use;
            return null;
        }

        SetHint(defaultHintKey);
        return hotkey;
    }

    /// <summary>Back to the plain hint, e.g. after the field was cleared.</summary>
    public void Reset() => SetHint(defaultHintKey);

    private void SetHint(string key)
    {
        _conflict = null;
        _hintKey = key;
    }
}
