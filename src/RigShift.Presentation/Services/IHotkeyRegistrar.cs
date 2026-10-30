using RigShift.Core.Profiles;

namespace RigShift.App.Services;

/// <summary>Windows' side of the hotkeys, so the bookkeeping in <see cref="HotkeyService"/> runs in tests without taking real keys.</summary>
public interface IHotkeyRegistrar : IDisposable
{
    /// <summary>The registration id of the combination that was pressed.</summary>
    event EventHandler<int>? Pressed;

    bool Register(int id, Hotkey hotkey, out int win32Error);

    void Unregister(int id);
}
