namespace RigShift.Core.Abstractions;

/// <summary>OS boundary for starting and ending programs. Paths may contain environment variables.</summary>
public interface IAppLauncher
{
    /// <summary>True when a process with the program's file name (without extension) runs.</summary>
    bool IsRunning(string path);

    void Start(string path, string? arguments);

    /// <summary>Closes the program's windows and ends what is still running after <paramref name="grace"/>.</summary>
    /// <returns>False when a process could not be ended (e.g. it runs elevated).</returns>
    Task<bool> StopAsync(string path, TimeSpan grace, CancellationToken cancellationToken);
}
