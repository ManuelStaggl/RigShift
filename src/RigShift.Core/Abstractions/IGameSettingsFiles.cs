namespace RigShift.Core.Abstractions;

/// <summary>Reads and writes single values in a game's own JSON settings file (see <c>GameSettings</c>).</summary>
public interface IGameSettingsFiles
{
    /// <summary>
    /// Reads the current values of <paramref name="keys"/>, then writes <paramref name="values"/> over them – after a
    /// backup, and only where something changes. Checks the result by reading the file again.
    /// </summary>
    /// <returns>The values as they were before the write.</returns>
    Task<IReadOnlyDictionary<string, string>> SwapAsync(
        string file, IReadOnlyList<string> keys, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken);
}
