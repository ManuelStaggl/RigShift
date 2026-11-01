using RigShift.Windows.Audio;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>Read-only against the machine's audio: CI runners and the server have no playback device or no sessions.</summary>
public sealed class SessionAppVolumesTests
{
    [Fact]
    public void AppsWithSound_ListsFileNames_WithoutThrowing() =>
        new SessionAppVolumes(Logger.None).AppsWithSound().ShouldAllBe(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Adjust_ProgramWithoutSound_SetsNothing() =>
        new SessionAppVolumes(Logger.None).Adjust("RigShift-test-no-such-program.exe", 50, muted: false).ShouldBe(0);
}
