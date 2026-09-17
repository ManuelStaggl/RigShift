using RigShift.Core.Fov;
using RigShift.Core.Profiles;

namespace RigShift.Core.Abstractions;

/// <summary>Physical picture size of a monitor, as its EDID reports it.</summary>
public interface IDisplaySizeReader
{
    /// <returns>The size, or <c>null</c> when the monitor reports none (virtual displays, remote sessions).</returns>
    ScreenSize? Read(DisplayIdentity display);
}
