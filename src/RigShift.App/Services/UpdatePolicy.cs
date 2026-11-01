using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;
using Serilog;

namespace RigShift.App.Services;

/// <summary>What an administrator has decided about updates; read once at startup.</summary>
public interface IUpdatePolicy
{
    /// <summary>RigShift never contacts GitHub: no check, no download, no "Check for updates".</summary>
    bool ChecksDisabled { get; }
}

/// <summary>
/// Reads <c>Software\Policies\RigShift\DisableUpdateCheck</c> (DWORD 1) from HKLM, then HKCU. The machine value wins,
/// also when it is 0 – as with every Windows policy, the user cannot overrule the administrator.
/// </summary>
public sealed class RegistryUpdatePolicy : IUpdatePolicy
{
    internal const string KeyPath = @"Software\Policies\RigShift";
    internal const string ValueName = "DisableUpdateCheck";

    public RegistryUpdatePolicy(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        log = log.ForContext<RegistryUpdatePolicy>();
        ChecksDisabled = IsDisabled(Read(Registry.LocalMachine, log), Read(Registry.CurrentUser, log));
        if (ChecksDisabled)
        {
            log.Information("Update checks are switched off by policy ({Key}\\{Value})", KeyPath, ValueName);
        }
    }

    public bool ChecksDisabled { get; }

    internal static bool IsDisabled(object? machineValue, object? userValue) => AsFlag(machineValue) ?? AsFlag(userValue) ?? false;

    /// <summary>Null for a missing or unreadable value, so the next source decides.</summary>
    private static bool? AsFlag(object? value) => value switch
    {
        int number => number == 1,
        string text when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) => number == 1,
        _ => null,
    };

    private static object? Read(RegistryKey hive, ILogger log)
    {
        try
        {
            using RegistryKey? key = hive.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Warning(ex, "Update policy under {Hive} could not be read", hive.Name);
            return null;
        }
    }
}
