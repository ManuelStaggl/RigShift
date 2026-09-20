using RigShift.Core.Abstractions;

namespace RigShift.Core.Tests.Fakes;

/// <summary>A started game that runs for <paramref name="lifetime"/> on the test's clock and then ends.</summary>
internal sealed class FakeRunningGame(TimeProvider time, TimeSpan lifetime, int id = 4711) : IRunningGame
{
    public int Id { get; } = id;

    public bool Disposed { get; private set; }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.Delay(lifetime, time, cancellationToken);

    public void Dispose() => Disposed = true;
}
