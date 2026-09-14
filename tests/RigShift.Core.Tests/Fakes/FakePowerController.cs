using RigShift.Core.Abstractions;

namespace RigShift.Core.Tests.Fakes;

/// <summary>Remembers the keep-awake request like the OS would.</summary>
internal sealed class FakePowerController : IPowerController
{
    public bool Fail { get; set; }

    public bool IsKeepingAwake { get; private set; }

    public void SetKeepAwake(bool keepAwake)
    {
        if (Fail)
        {
            throw new InvalidOperationException("power API failed");
        }

        IsKeepingAwake = keepAwake;
    }
}
