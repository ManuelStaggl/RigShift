using Microsoft.Win32;

namespace RigShift.Windows.Games;

/// <summary>Where Windows keeps its uninstall entries, for everything that reads them, so all readers agree on one entry.</summary>
internal static class UninstallRegistry
{
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Machine-wide 64-bit programs first, then 32-bit ones (Steam is one, so its <c>Steam App</c> entries land there), then
    /// per-user installs.
    /// </summary>
    public static readonly (RegistryHive Hive, RegistryView View)[] Views =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Default),
    ];
}
