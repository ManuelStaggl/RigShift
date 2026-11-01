namespace RigShift.Core.Abstractions;

/// <summary>OS boundary for the volume of single programs (audio sessions on every playback device).</summary>
public interface IAppVolumes
{
    /// <summary>File names of the programs that have sound open right now, e.g. <c>Discord.exe</c>; empty without audio.</summary>
    IReadOnlyList<string> AppsWithSound();

    /// <summary>
    /// Sets every audio session of programs with this file name. Returns how many sessions were set; 0 when the program
    /// has no sound open. Never throws; failures are logged.
    /// </summary>
    int Adjust(string executable, int volumePercent, bool muted);
}
