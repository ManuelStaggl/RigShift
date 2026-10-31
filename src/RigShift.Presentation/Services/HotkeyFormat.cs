using System.Globalization;
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

    private static string KeyName(int virtualKey) => virtualKey switch
    {
        // VK_0..VK_9 and VK_A..VK_Z are the characters themselves.
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (virtualKey - 0x60).ToString(CultureInfo.InvariantCulture),
        >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x6F).ToString(CultureInfo.InvariantCulture),
        // Everything else as the keyboard layout names it: "," instead of OemComma, "Page Up" instead of Prior.
        _ => LayoutName(virtualKey) ?? "0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture),
    };

    // Some layouts name keys in capitals ("BILD-AUF"); shown like the others ("Bild-Auf").
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
