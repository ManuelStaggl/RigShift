using RigShift.App.Services;

namespace RigShift.App.Tests;

/// <summary>A session the test locks and unlocks.</summary>
internal sealed class FakeSessionWatch : ISessionWatch
{
    private bool _interactive = true;

    public event EventHandler? Changed;

    public bool IsInteractive
    {
        get => _interactive;
        set
        {
            if (_interactive != value)
            {
                _interactive = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
