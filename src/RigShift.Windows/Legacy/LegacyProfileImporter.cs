using System.Runtime.InteropServices;
using RigShift.Core.Legacy;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using RigShift.Windows.Display;
using Serilog;
using Windows.Win32;
using Windows.Win32.Devices.Display;

namespace RigShift.Windows.Legacy;

/// <summary>
/// Turns the script's <c>.display</c> files plus <c>DisplayProfiles.json</c> into RigShift profiles.
/// The originals are only read, never changed – they stay the fallback until the user removes them.
/// </summary>
public static class LegacyProfileImporter
{
    private const string PciAdapterPrefix = @"\\?\PCI#";

    /// <summary>Imports every <c>*.display</c> file in <paramref name="folder"/>. One broken file does not stop the others.</summary>
    /// <param name="live">Optional current snapshot, used to fill in EDID IDs and names the legacy format lacks.</param>
    public static IReadOnlyList<LegacyImportResult> ImportFolder(string folder, DisplaySnapshot? live, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(log);

        string directory = Path.GetFullPath(folder);
        string audioFile = Path.GetFullPath(Path.Combine(directory, "DisplayProfiles.json"));
        IReadOnlyDictionary<string, AudioEndpoint> audio = new Dictionary<string, AudioEndpoint>();

        if (File.Exists(audioFile))
        {
            try
            {
                audio = LegacyAudioConfig.Parse(File.ReadAllText(audioFile));
            }
            catch (System.Text.Json.JsonException ex)
            {
                log.Warning(ex, "Legacy audio configuration {File} is invalid, importing without audio", audioFile);
            }
        }

        var results = new List<LegacyImportResult>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.display").Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            try
            {
                // ReadAllText strips the UTF-8 BOM that Windows PowerShell 5.1 writes.
                LegacyDisplayProfile legacy = LegacyDisplayFile.Parse(File.ReadAllText(file));
                Profile profile = Import(name, legacy, audio.GetValueOrDefault(name), live);
                log.Information("Imported legacy profile {Profile} from {File}: {Displays} displays", name, file, profile.Displays.Count);
                results.Add(new LegacyImportResult(name, file, profile, null));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                log.Warning(ex, "Legacy profile {File} could not be imported", file);
                results.Add(new LegacyImportResult(name, file, null, ex.Message));
            }
        }

        return results;
    }

    public static Profile Import(string name, LegacyDisplayProfile legacy, AudioEndpoint? playback, DisplaySnapshot? live)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(legacy);

        var displays = new List<DisplayAssignment>();
        foreach (LegacyPathEntry entry in legacy.Paths)
        {
            if (Decode(entry, legacy.Modes, live) is { } assignment)
            {
                displays.Add(assignment);
            }
        }

        if (displays.Count == 0)
        {
            throw new FormatException("Legacy profile contains no active display with a source mode.");
        }

        return new Profile
        {
            Id = Guid.NewGuid(),
            Name = name,
            Displays = displays,
            Audio = new AudioAssignment { Playback = playback },
        };
    }

    internal static DisplayAssignment? Decode(LegacyPathEntry entry, IReadOnlyList<LegacyModeEntry> modes, DisplaySnapshot? live)
    {
        DISPLAYCONFIG_PATH_INFO path = ReadStruct<DISPLAYCONFIG_PATH_INFO>(entry.PathStruct, "path");
        if ((path.flags & PInvoke.DISPLAYCONFIG_PATH_ACTIVE) == 0)
        {
            return null;
        }

        uint sourceIndex = path.sourceInfo.modeInfoIdx;
        if (sourceIndex >= modes.Count)
        {
            throw new FormatException($"Display '{entry.FriendlyName}' references source mode {sourceIndex}, but the file has {modes.Count} modes.");
        }

        DISPLAYCONFIG_MODE_INFO sourceMode = ReadStruct<DISPLAYCONFIG_MODE_INFO>(modes[(int)sourceIndex].ModeStruct, "mode");
        if (sourceMode.infoType != DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
        {
            throw new FormatException($"Display '{entry.FriendlyName}': mode {sourceIndex} is not a source mode.");
        }

        DISPLAYCONFIG_RATIONAL refresh = path.targetInfo.refreshRate;
        uint targetIndex = path.targetInfo.modeInfoIdx;
        if (targetIndex < modes.Count)
        {
            DISPLAYCONFIG_MODE_INFO targetMode = ReadStruct<DISPLAYCONFIG_MODE_INFO>(modes[(int)targetIndex].ModeStruct, "mode");
            if (targetMode.infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
            {
                refresh = targetMode.targetMode.targetVideoSignalInfo.vSyncFreq;
            }
        }

        AttachedDisplay? known = live?.Displays.FirstOrDefault(d =>
            string.Equals(d.Identity.TargetDevicePath, entry.TargetDevicePath, StringComparison.OrdinalIgnoreCase));

        var identity = new DisplayIdentity
        {
            AdapterDevicePath = entry.TargetAdapterPath,
            TargetDevicePath = entry.TargetDevicePath,
            EdidManufacturerId = known?.Identity.EdidManufacturerId ?? 0,
            EdidProductCodeId = known?.Identity.EdidProductCodeId ?? 0,
            EdidSerialHash = known?.Identity.EdidSerialHash,
            FriendlyName = entry.FriendlyName.Length > 0 ? entry.FriendlyName : known?.Identity.FriendlyName ?? string.Empty,
        };

        return CcdModes.ToAssignment(identity, sourceMode.sourceMode, refresh, path.targetInfo.rotation) with
        {
            // Displays on non-PCI adapters are virtual (spacedesk & co.) and only exist while connected.
            IsOptional = !entry.TargetAdapterPath.StartsWith(PciAdapterPrefix, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static T ReadStruct<T>(byte[] bytes, string kind)
        where T : unmanaged
    {
        int expected = Marshal.SizeOf<T>();
        return bytes.Length == expected
            ? MemoryMarshal.Read<T>(bytes)
            : throw new FormatException($"Legacy {kind} struct has {bytes.Length} bytes, expected {expected}.");
    }
}

public sealed record LegacyImportResult(string Name, string File, Profile? Profile, string? Error);
