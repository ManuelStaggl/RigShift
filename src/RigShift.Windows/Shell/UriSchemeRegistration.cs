using System.Security;
using Microsoft.Win32;
using RigShift.Core.Cli;

namespace RigShift.Windows.Shell;

/// <summary>
/// Registers <c>rigshift://</c> for the current user (<c>HKCU\Software\Classes</c>, no elevation). Called from the
/// Velopack install/update hooks, so the command always points at the installed executable; portable copies do not
/// register. Hooks run before logging exists, so failures are reported as the return value only.
/// </summary>
public static class UriSchemeRegistration
{
    private static readonly string KeyPath = @"Software\Classes\" + RigShiftUri.Scheme;

    public static bool Register(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key.SetValue(string.Empty, "URL:RigShift", RegistryValueKind.String);
            key.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);

            using RegistryKey icon = key.CreateSubKey("DefaultIcon", writable: true);
            icon.SetValue(string.Empty, "\"" + executablePath + "\",0", RegistryValueKind.String);

            using RegistryKey command = key.CreateSubKey(@"shell\open\command", writable: true);
            command.SetValue(string.Empty, "\"" + executablePath + "\" \"%1\"", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public static bool Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }
}
