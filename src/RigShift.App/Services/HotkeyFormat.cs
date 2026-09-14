using System.Globalization;
using System.Windows.Input;
using RigShift.App.Localization;
using RigShift.Core.Profiles;

namespace RigShift.App.Services;

/// <summary>Hotkeys as the user sees them, e.g. <c>Strg+Alt+F1</c>, and conversion from WPF key events.</summary>
public static class HotkeyFormat
{
    public static string Format(Hotkey hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);

        var parts = new List<string>(5);
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add(Loc.Instance["Key_Ctrl"]);
        }

        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add(Loc.Instance["Key_Shift"]);
        }

        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(hotkey.VirtualKey));
        return string.Join('+', parts);
    }

    /// <summary>WPF's <see cref="ModifierKeys"/> use the same bit values as the Win32 <c>MOD_*</c> flags.</summary>
    public static HotkeyModifiers FromWpf(ModifierKeys modifiers) => (HotkeyModifiers)(int)modifiers;

    private static string KeyName(int virtualKey)
    {
        Key key = KeyInterop.KeyFromVirtualKey(virtualKey);
        return key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + ((int)(key - Key.NumPad0)).ToString(CultureInfo.InvariantCulture),
            >= Key.A and <= Key.Z or >= Key.F1 and <= Key.F24 => key.ToString(),
            // Everything else as the keyboard layout names it: "," instead of OemComma, "Page Up" instead of Prior (I-14).
            _ => LayoutName(virtualKey) ?? (key == Key.None ? "0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture) : key.ToString()),
        };
    }

    /// <summary>Some layouts name keys in capitals ("BILD-AUF"); shown like the other key names ("Bild-Auf").</summary>
    private static string? LayoutName(int virtualKey)
    {
        string? name = RigShift.Windows.Ui.NativeWindow.KeyName(virtualKey);
        if (name is null || name.Length < 2 || name.Any(char.IsLower))
        {
            return name;
        }

        CultureInfo culture = Loc.Instance.Culture;
        return culture.TextInfo.ToTitleCase(name.ToLower(culture));
    }
}
