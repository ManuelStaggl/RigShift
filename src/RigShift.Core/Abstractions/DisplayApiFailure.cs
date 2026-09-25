using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RigShift.Core.Abstractions;

/// <summary>
/// What the Windows display layer (<see cref="IDisplayConfigurator"/>, <see cref="ISurroundController"/>) throws when a
/// query or an apply goes wrong (analysis finding B-07). Anything else is a bug and should surface as one.
/// </summary>
public static class DisplayApiFailure
{
    public static bool Is(Exception exception) => exception is Win32Exception or COMException;
}
