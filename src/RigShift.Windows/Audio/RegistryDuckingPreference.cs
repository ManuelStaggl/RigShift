using Microsoft.Win32;
using RigShift.Core.Abstractions;
using Serilog;

namespace RigShift.Windows.Audio;

/// <summary>
/// <see cref="IDuckingPreference"/> over HKCU <c>Software\Microsoft\Multimedia\Audio</c>, DWORD <c>UserDuckingPreference</c> –
/// the value the Sound control panel ("Communications" tab) writes. Undocumented; Windows reads it when a
/// communications stream starts, so a call already running may keep the old behavior.
/// </summary>
public sealed class RegistryDuckingPreference : IDuckingPreference
{
    private const string KeyPath = @"Software\Microsoft\Multimedia\Audio";
    private const string ValueName = "UserDuckingPreference";

    private readonly ILogger _log;

    public RegistryDuckingPreference(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<RegistryDuckingPreference>();
    }

    public int? Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is int value ? value : null;
    }

    public void Write(int? value)
    {
        if (value is { } preference)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(ValueName, preference, RegistryValueKind.DWord);
            _log.Information("Communications ducking preference set to {Preference}", preference);
        }
        else
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            _log.Information("Communications ducking preference removed (Windows default)");
        }
    }
}
