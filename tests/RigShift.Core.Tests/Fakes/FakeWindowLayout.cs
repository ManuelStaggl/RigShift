using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;

namespace RigShift.Core.Tests.Fakes;

/// <summary>A desktop the test drives: <see cref="Windows"/> is what <see cref="Open"/> returns.</summary>
internal sealed class FakeWindowLayout : IWindowLayout
{
    public List<OpenWindow> Windows { get; } = [];

    /// <summary>Handles that refuse to be moved – an elevated program, or one that is not responding.</summary>
    public HashSet<nint> Refuse { get; } = [];

    /// <summary>Where each handle was placed, in the order the calls came in.</summary>
    public List<(nint Handle, PixelRect Bounds, WindowState State)> Placed { get; } = [];

    /// <summary>Called with the number of <see cref="Open"/> calls so far, before the list is returned.</summary>
    public Action<int>? OnOpened { get; set; }

    public int OpenCalls { get; private set; }

    public IReadOnlyList<OpenWindow> Open()
    {
        OpenCalls++;
        OnOpened?.Invoke(OpenCalls);
        return [.. Windows];
    }

    public bool Place(nint windowHandle, PixelRect bounds, WindowState state)
    {
        if (Refuse.Contains(windowHandle))
        {
            return false;
        }

        Placed.Add((windowHandle, bounds, state));
        return true;
    }
}
