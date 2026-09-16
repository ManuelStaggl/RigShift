using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;

namespace RigShift.Core.Tests.Fakes;

/// <summary>
/// A desktop whose symbols stay where they are put – unless <see cref="MovesAfterRestore"/> says Explorer rearranges
/// them once more, which is what the retry in the orchestrator is for.
/// </summary>
public sealed class FakeDesktopIcons : IDesktopIcons
{
    private readonly List<DesktopIcon> _icons = [];

    /// <summary>How often the restored layout is scrambled again before it is left alone.</summary>
    public int MovesAfterRestore { get; set; }

    /// <summary>What <see cref="Restore"/> answers; the default is a plain success.</summary>
    public DesktopIconOutcome Outcome { get; set; } = DesktopIconOutcome.Restored;

    /// <summary>Set to false for a session without a desktop (a service, SSH).</summary>
    public bool Reachable { get; set; } = true;

    public int Restores { get; private set; }

    public int Captures { get; private set; }

    private int _capturesSinceRestore;

    public DesktopIconLayout? Capture()
    {
        Captures++;
        if (!Reachable)
        {
            return null;
        }

        // Not on the first look after a restore: that one is what the shell made of the wish, and the caller keeps it
        // as its reference. Explorer's own re-layout lands between that look and the next one.
        if (MovesAfterRestore > 0 && _capturesSinceRestore >= 1)
        {
            MovesAfterRestore--;
            for (int i = 0; i < _icons.Count; i++)
            {
                _icons[i] = _icons[i] with { X = _icons[i].X + 100 };
            }
        }

        _capturesSinceRestore++;
        return new DesktopIconLayout { Icons = [.. _icons], CapturedAt = DateTimeOffset.UnixEpoch };
    }

    public DesktopIconResult Restore(DesktopIconLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Restores++;
        _capturesSinceRestore = 0;
        if (!Reachable)
        {
            return DesktopIconResult.Unavailable;
        }

        if (Outcome != DesktopIconOutcome.Restored)
        {
            return new DesktopIconResult(Outcome, 0, 0);
        }

        _icons.Clear();
        _icons.AddRange(layout.Icons);
        return new DesktopIconResult(DesktopIconOutcome.Restored, _icons.Count, 0);
    }
}
