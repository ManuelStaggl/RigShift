using Microsoft.Win32;
using RigShift.Core.Abstractions;
using Serilog;

namespace RigShift.Windows.Startup;

/// <summary>
/// <see cref="IAutostart"/> via <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> – per user, no elevation.
/// The stored command points at the running executable, so a moved portable EXE re-registers when toggled again.
/// </summary>
public sealed class RunKeyAutostart : IAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RigShift";

    private readonly string _command;
    private readonly ILogger _log;

    public RunKeyAutostart(string executablePath, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(log);
        _command = "\"" + executablePath + "\" --minimized";
        _log = log.ForContext<RunKeyAutostart>();
    }

    public bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, _command, RegistryValueKind.String);
            _log.Information("Autostart enabled: {Command}", _command);
        }
        else
        {
            Disable(_log);
        }
    }

    /// <summary>Removes the autostart entry, e.g. before uninstalling, so it does not point at a deleted executable.</summary>
    public static void Disable(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        log.ForContext<RunKeyAutostart>().Information("Autostart disabled");
    }
}
