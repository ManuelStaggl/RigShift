using RigShift.App.Services;
using RigShift.Core.Profiles;

namespace RigShift.App.Tests;

/// <summary>A registrar that takes no real keys: it only remembers what it was given.</summary>
internal sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    public Dictionary<int, Hotkey> Held { get; } = [];

    /// <summary>Combinations another application holds: registering them fails with 1409.</summary>
    public HashSet<Hotkey> TakenElsewhere { get; } = [];

#pragma warning disable CS0067 // Nothing presses a key in these tests.
    public event EventHandler<int>? Pressed;
#pragma warning restore CS0067

    public bool Register(int id, Hotkey hotkey, out int error)
    {
        if (TakenElsewhere.Contains(hotkey) || Held.ContainsValue(hotkey))
        {
            error = 1409;
            return false;
        }

        error = 0;
        Held[id] = hotkey;
        return true;
    }

    public void Unregister(int id) => Held.Remove(id);

    public void Dispose()
    {
    }
}
