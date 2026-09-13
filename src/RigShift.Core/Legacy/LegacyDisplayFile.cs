namespace RigShift.Core.Legacy;

/// <summary>
/// Reader for the <c>.display</c> files written by the original PowerShell script (see <c>legacy/DisplayProfile.ps1</c>).
/// Line format, pipe-separated:
/// <code>
/// P|&lt;base64 DISPLAYCONFIG_PATH_INFO&gt;|&lt;source adapter path&gt;|&lt;target adapter path&gt;|&lt;target device path&gt;|&lt;friendly name&gt;
/// M|&lt;base64 DISPLAYCONFIG_MODE_INFO&gt;|&lt;adapter path&gt;|&lt;target device path or empty&gt;
/// </code>
/// Only the text fields are parsed here; decoding the native structs is the job of the Windows layer,
/// because the struct layout is platform-specific.
/// </summary>
public static class LegacyDisplayFile
{
    public static LegacyDisplayProfile Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var paths = new List<LegacyPathEntry>();
        var modes = new List<LegacyModeEntry>();

        foreach (string rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = rawLine.Split('|');
            switch (fields[0])
            {
                case "P" when fields.Length >= 6:
                    paths.Add(new LegacyPathEntry(
                        Convert.FromBase64String(fields[1]),
                        fields[2],
                        fields[3],
                        fields[4],
                        fields[5]));
                    break;
                case "M" when fields.Length >= 4:
                    modes.Add(new LegacyModeEntry(
                        Convert.FromBase64String(fields[1]),
                        fields[2],
                        fields[3]));
                    break;
                default:
                    throw new FormatException($"Unrecognised line in legacy .display file: '{Truncate(rawLine)}'.");
            }
        }

        if (paths.Count == 0)
        {
            throw new FormatException("Legacy .display file contains no display paths.");
        }

        return new LegacyDisplayProfile(paths, modes);
    }

    private static string Truncate(string line) => line.Length <= 40 ? line : line[..40] + "…";
}

public sealed record LegacyDisplayProfile(IReadOnlyList<LegacyPathEntry> Paths, IReadOnlyList<LegacyModeEntry> Modes);

public sealed record LegacyPathEntry(
    byte[] PathStruct,
    string SourceAdapterPath,
    string TargetAdapterPath,
    string TargetDevicePath,
    string FriendlyName);

public sealed record LegacyModeEntry(byte[] ModeStruct, string AdapterPath, string TargetDevicePath);
