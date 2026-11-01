using RigShift.App.Services;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

public sealed class DesktopIconWatcherTests : IDisposable
{
    private static readonly DesktopIconLayout Moved = Layout(("C:/Users/sim/Desktop/SimHub.lnk", 20, 20), ("::{645FF040-5081-101B-9F08-00AA002F954E}", 20, 140));
    private static readonly DesktopIconLayout Saved = Layout(("C:/Users/sim/Desktop/SimHub.lnk", 900, 20), ("::{645FF040-5081-101B-9F08-00AA002F954E}", 20, 140));

    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly ManualClock _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MovedIcons_AreSavedToTheActiveProfile_OnceTheyHeldStill()
    {
        using DesktopIconWatcher watcher = await CreateAsync(Profile("Desk", DeskModes) with { DesktopIcons = Saved });
        _host.DesktopIcons.Restore(Moved);

        await watcher.CheckAsync();
        StoredIcons().ShouldBe(Saved, "one look could be the middle of a drag");

        await watcher.CheckAsync();
        StoredIcons().ShouldNotBeNull().SamePositions(Moved).ShouldBeTrue();
    }

    [Fact]
    public async Task AProfileWithoutIcons_GetsTheLayoutToo()
    {
        using DesktopIconWatcher watcher = await CreateAsync(Profile("Desk", DeskModes));
        _host.DesktopIcons.Restore(Moved);

        await watcher.CheckAsync();
        await watcher.CheckAsync();

        StoredIcons().ShouldNotBeNull().SamePositions(Moved).ShouldBeTrue();
    }

    /// <summary>Explorer reshuffles the icons after a display change; that is not the user's arrangement.</summary>
    [Fact]
    public async Task AfterADisplayChange_NothingIsSavedUntilItSettled()
    {
        using DesktopIconWatcher watcher = await CreateAsync(Profile("Desk", DeskModes) with { DesktopIcons = Saved });
        _host.DesktopIcons.Restore(Moved);

        watcher.Quiet();
        await watcher.CheckAsync();
        await watcher.CheckAsync();
        StoredIcons().ShouldBe(Saved);

        _time.Now += DesktopIconWatcher.SettleTime;
        await watcher.CheckAsync();
        await watcher.CheckAsync();
        StoredIcons().ShouldNotBeNull().SamePositions(Moved).ShouldBeTrue();
    }

    [Fact]
    public async Task AProfileThatDoesNotRememberIcons_IsLeftAlone()
    {
        using DesktopIconWatcher watcher = await CreateAsync(Profile("Desk", DeskModes) with { RememberDesktopIcons = false });
        _host.DesktopIcons.Restore(Moved);

        await watcher.CheckAsync();
        await watcher.CheckAsync();

        StoredIcons().ShouldBeNull();
        _host.DesktopIcons.Captures.ShouldBe(0);
    }

    [Fact]
    public async Task UnchangedIcons_AreNotSavedAgain()
    {
        using DesktopIconWatcher watcher = await CreateAsync(Profile("Desk", DeskModes) with { DesktopIcons = Saved });
        _host.DesktopIcons.Restore(Saved);

        await watcher.CheckAsync();
        await watcher.CheckAsync();

        StoredIcons().ShouldBeSameAs(Saved);
    }

    public void Dispose() => _host.Dispose();

    private async Task<DesktopIconWatcher> CreateAsync(Profile desk)
    {
        _host.Store.Profiles.Add(desk);
        await _host.Catalog.ReloadAsync(Ct);
        _host.Catalog.ActiveProfile.ShouldNotBeNull().Id.ShouldBe(desk.Id);
        var watcher = new DesktopIconWatcher(_host.Catalog, _host.Coordinator, _host.DesktopIcons, _time, Logger.None);
        _time.Now += DesktopIconWatcher.SettleTime;
        return watcher;
    }

    private DesktopIconLayout? StoredIcons() => _host.Store.Profiles.ShouldHaveSingleItem().DesktopIcons;

    private static DesktopIconLayout Layout(params (string Item, int X, int Y)[] icons) =>
        new() { Icons = [.. icons.Select(i => new DesktopIcon { Item = i.Item, X = i.X, Y = i.Y })], CapturedAt = DateTimeOffset.UnixEpoch };

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
