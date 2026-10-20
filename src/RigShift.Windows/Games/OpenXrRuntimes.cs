using System.Text.Json;
using Microsoft.Win32;
using RigShift.Core.Abstractions;
using Serilog;

namespace RigShift.Windows.Games;

/// <summary>
/// <see cref="IOpenXrRuntimes"/> from <c>HKLM\SOFTWARE\Khronos\OpenXR\1</c>: <c>AvailableRuntimes</c> (value name = manifest
/// path, 0 = enabled) and <c>ActiveRuntime</c>. Only what an administrator registered there counts – a manifest names
/// a DLL the game loads, so a path from anywhere else is not one to hand over.
/// </summary>
public sealed class OpenXrRuntimes : IOpenXrRuntimes
{
    private const string KeyPath = @"SOFTWARE\Khronos\OpenXR\1";
    private readonly ILogger _log;

    public OpenXrRuntimes(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<OpenXrRuntimes>();
    }

    public IReadOnlyList<OpenXrRuntime> Installed()
    {
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = hklm.OpenSubKey(KeyPath);
            if (key is null)
            {
                return [];
            }

            string? active = key.GetValue("ActiveRuntime") as string;
            var paths = new List<string>();
            using (RegistryKey? available = key.OpenSubKey("AvailableRuntimes"))
            {
                foreach (string name in available?.GetValueNames() ?? [])
                {
                    if (available!.GetValue(name) is int disabled && disabled == 0)
                    {
                        paths.Add(name);
                    }
                }
            }

            if (active is { Length: > 0 })
            {
                paths.Add(active);
            }

            return [.. paths
                .Select(p => Environment.ExpandEnvironmentVariables(p))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => new OpenXrRuntime(p, NameOf(p), active is not null && string.Equals(p, Environment.ExpandEnvironmentVariables(active), StringComparison.OrdinalIgnoreCase)))
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Warning(ex, "OpenXR runtimes could not be read");
            return [];
        }
    }

    /// <summary>The runtime's own name from the manifest, else the file name without extension.</summary>
    public static string NameOf(string manifestPath)
    {
        try
        {
            using FileStream stream = File.OpenRead(manifestPath);
            using JsonDocument json = JsonDocument.Parse(stream);
            if (json.RootElement.TryGetProperty("runtime", out JsonElement runtime)
                && runtime.ValueKind == JsonValueKind.Object
                && runtime.TryGetProperty("name", out JsonElement name)
                && name.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A manifest that cannot be read still names a runtime; the file name says which.
        }

        return Path.GetFileNameWithoutExtension(manifestPath);
    }
}
