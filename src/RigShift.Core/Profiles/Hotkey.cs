using System.Text.Json.Serialization;

namespace RigShift.Core.Profiles;

/// <summary>Modifier keys of a <see cref="Hotkey"/>; the values match the Win32 <c>MOD_*</c> flags of <c>RegisterHotKey</c>.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

/// <summary>
/// A system-wide key combination that switches to a profile. Ctrl, Alt or Win is required:
/// a plain key – or Shift+A, the capital A – would be taken away from every other application.
/// </summary>
public sealed record Hotkey
{
    private const HotkeyModifiers AllModifiers = HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows;
    private const HotkeyModifiers RequiredModifiers = HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Windows;

    public required HotkeyModifiers Modifiers { get; init; }

    /// <summary>Win32 virtual-key code of the non-modifier key.</summary>
    public required int VirtualKey { get; init; }

    /// <summary>Computed, so not stored: settings and profiles written before 4.0 carry it and are read as before.</summary>
    [JsonIgnore]
    public bool IsValid =>
        (Modifiers & RequiredModifiers) != HotkeyModifiers.None
        && (Modifiers & ~AllModifiers) == HotkeyModifiers.None
        && VirtualKey is > 0 and < 0xFF
        && !IsModifierKey(VirtualKey);

    /// <summary>Shift, Ctrl, Alt and the Windows keys, in their generic and left/right forms.</summary>
    public static bool IsModifierKey(int virtualKey) => virtualKey is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C;
}
