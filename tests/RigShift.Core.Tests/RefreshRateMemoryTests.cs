using RigShift.Core.Profiles;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class RefreshRateMemoryTests
{
    [Fact]
    public void With_ThenGet_ReturnsTheRatesForThatResolutionOnly()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>>? memory =
            RefreshRateMemory.With(null, Tablet, 1920, 1080, [new RefreshRate(60000, 1000), new RefreshRate(30, 1)]);

        RefreshRateMemory.Get(memory, Tablet, 1920, 1080).ShouldBe([new RefreshRate(60000, 1000), new RefreshRate(30, 1)]);
        RefreshRateMemory.Get(memory, Tablet, 1280, 720).ShouldBeEmpty();
        RefreshRateMemory.Get(memory, Ultrawide, 1920, 1080).ShouldBeEmpty();
    }

    [Fact]
    public void With_SameOrNoRates_ReturnsTheSameInstance()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>>? memory = RefreshRateMemory.With(null, Tablet, 1920, 1080, [new RefreshRate(60, 1)]);

        RefreshRateMemory.With(memory, Tablet, 1920, 1080, [new RefreshRate(60, 1)]).ShouldBeSameAs(memory);
        RefreshRateMemory.With(memory, Tablet, 1920, 1080, []).ShouldBeSameAs(memory);
    }

    [Fact]
    public void Get_IgnoresBrokenEntries()
    {
        var memory = new Dictionary<string, IReadOnlyList<string>>
        {
            [RefreshRateMemory.Key(Tablet, 1920, 1080)] = ["60/1", "x", "30/0", "-1/1"],
        };

        RefreshRateMemory.Get(memory, Tablet, 1920, 1080).ShouldBe([new RefreshRate(60, 1)]);
    }
}
