using System.Globalization;
using System.Security;
using Microsoft.Win32;

namespace RigShift.Windows.Display;

/// <summary>A graphics adapter as Windows lists it in Device Manager, with its driver.</summary>
public sealed record GraphicsDriver(string Name, string? Provider, string? Version)
{
    /// <summary>
    /// "560.94" for an NVIDIA driver: the number NVIDIA publishes and forum posts ask for. Windows only keeps the
    /// file version (32.0.15.6094); the release is the last five digits of its last two parts.
    /// </summary>
    public string? NvidiaRelease
    {
        get
        {
            string[] parts = Version?.Split('.') ?? [];
            if (!string.Equals(Provider, "NVIDIA", StringComparison.OrdinalIgnoreCase) || parts.Length != 4)
            {
                return null;
            }

            string digits = parts[2] + parts[3].PadLeft(4, '0');
            return digits.Length < 5 || !digits.All(char.IsAsciiDigit)
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"{digits[^5..^2]}.{digits[^2..]}");
        }
    }

    public override string ToString()
    {
        string text = Version is null ? Name : $"{Name}, driver {Version}";
        return NvidiaRelease is { } release ? $"{text} ({release})" : text;
    }
}

public static class GraphicsDrivers
{
    /// <summary>The display adapter class; every adapter and its driver sit in a numbered subkey.</summary>
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Every display adapter with a driver, each once; empty when the registry cannot be read.</summary>
    public static IReadOnlyList<GraphicsDriver> Read()
    {
        var drivers = new List<GraphicsDriver>();
        try
        {
            using RegistryKey? displayClass = Registry.LocalMachine.OpenSubKey(DisplayClass);
            foreach (string name in displayClass?.GetSubKeyNames() ?? [])
            {
                if (name.Length != 4 || !name.All(char.IsAsciiDigit))
                {
                    continue; // "Properties" and the like: not an adapter, and not readable anyway.
                }

                try
                {
                    using RegistryKey? adapter = displayClass!.OpenSubKey(name);
                    if (adapter?.GetValue("DriverDesc") is string description)
                    {
                        drivers.Add(new GraphicsDriver(description, adapter.GetValue("ProviderName") as string, adapter.GetValue("DriverVersion") as string));
                    }
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
                {
                    // One unreadable adapter does not hide the others.
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }

        // Windows keeps a subkey per install of the same card (driver updates, remote sessions).
        return [.. drivers.Distinct()];
    }
}
