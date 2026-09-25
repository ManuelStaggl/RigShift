using System.ComponentModel;

namespace RigShift.Core.Abstractions;

/// <summary>
/// The graphics driver did not answer a display call in time, or an earlier call is still stuck in it (v4 finding K-07).
/// A <see cref="Win32Exception"/> with ERROR_TIMEOUT, so every place that handles a failed display call handles this too.
/// </summary>
public sealed class DisplayDriverHungException : Win32Exception
{
    /// <summary>ERROR_TIMEOUT.</summary>
    public const int ErrorTimeout = 1460;

    public DisplayDriverHungException()
        : base(ErrorTimeout, "The graphics driver does not answer. If the displays stay wrong, restart the PC.")
    {
    }

    public DisplayDriverHungException(string message)
        : base(ErrorTimeout, message)
    {
    }

    public DisplayDriverHungException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
