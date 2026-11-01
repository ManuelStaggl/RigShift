using RigShift.Core.Abstractions;

namespace RigShift.App.Tests;

/// <summary>A fixed list of OpenXR runtimes instead of the registry.</summary>
internal sealed class FakeOpenXrRuntimes(params OpenXrRuntime[] runtimes) : IOpenXrRuntimes
{
    public IReadOnlyList<OpenXrRuntime> Installed() => runtimes;
}
