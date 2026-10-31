using System.Globalization;
using Microsoft.Win32;
using RigShift.Core.Games;
using Serilog;

namespace RigShift.Windows.Games;

/// <summary>
/// The file a game's icon is taken from, for the cards, the picker and a desktop shortcut. A game shows itself far
/// better than any symbol we could pick for it.
/// </summary>
public static class GameIconSource
{
    /// <summary>The file holding the icon, or <c>null</c> when none was found – then the symbol stands in.</summary>
    public static string? Find(GameLaunch launch, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(log);

        return GameExecutable.Find(launch, log) ?? UninstallEntryIcon(launch, log);
    }

    // A Steam game before its first start has no known executable; Steam's "Steam App <appid>" uninstall entry has a
    // DisplayIcon.
    private static string? UninstallEntryIcon(GameLaunch launch, ILogger log)
    {
        if (launch.Kind != GameLaunchKind.Steam || launch.Target.Trim() is not { Length: > 0 } appId)
        {
            return null;
        }

        string subKey = string.Create(CultureInfo.InvariantCulture, $@"{UninstallRegistry.KeyPath}\Steam App {appId}");
        foreach ((RegistryHive hive, RegistryView view) in UninstallRegistry.Views)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? entry = root.OpenSubKey(subKey);
                if (entry?.GetValue("DisplayIcon") is string value && IconLocation.Parse(value) is { } location
                    && File.Exists(location.Path))
                {
                    return location.Path;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                log.Debug(ex, "Uninstall entry {Key} could not be read", subKey);
            }
        }

        return null;
    }
}

/// <summary>The shell's <c>path,index</c> notation, as it stands in <c>DisplayIcon</c> and in a shortcut.</summary>
public static class IconLocation
{
    /// <summary>Splits <c>"C:\Games\game.exe",0</c> into path and index; <c>null</c> when there is no usable path.</summary>
    public static (string Path, int Index)? Parse(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        int index = 0;
        int comma = text.LastIndexOf(',');

        // Only a trailing ",<number>" is an index; a comma inside a folder name is part of the path.
        if (comma >= 0 && int.TryParse(text.AsSpan(comma + 1).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed))
        {
            index = parsed;
            text = text[..comma].Trim();
        }

        text = text.Trim('"').Trim();
        return text.Length == 0 ? null : (text, index);
    }
}
