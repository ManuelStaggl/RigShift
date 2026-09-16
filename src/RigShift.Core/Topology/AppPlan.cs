using RigShift.Core.Games;
using RigShift.Core.Profiles;

namespace RigShift.Core.Topology;

/// <summary>
/// The apps side of a profile or of a game, as <see cref="AppRunner"/> needs it. The two carry the same three things,
/// so they share the runner instead of growing a second copy of the waiting, ordering and grace logic.
/// </summary>
/// <param name="Name">What the log calls this run – the profile's or the game's name.</param>
/// <param name="Apps">The actions, in order.</param>
/// <param name="WaitForUsbDeviceId">USB device the apps wait for, or <c>null</c>.</param>
/// <param name="WaitForUsbDeviceName">Its name when it was picked, for the messages while it is missing.</param>
internal sealed record AppPlan(
    string Name,
    IReadOnlyList<AppAction> Apps,
    string? WaitForUsbDeviceId,
    string? WaitForUsbDeviceName)
{
    public static AppPlan For(Profile profile) => new(
        profile.Name, profile.Apps, profile.AppsWaitForUsbDeviceId, profile.AppsWaitForUsbDeviceName);

    /// <summary>
    /// The apps of a game that run at <paramref name="when"/>. Only the ones before the game wait for the USB
    /// device: once the game runs, waiting half a minute for a wheelbase would hold up Crew Chief for no reason.
    /// </summary>
    public static AppPlan For(GameEntry game, AppTiming when) => new(
        game.Name,
        [.. game.Apps.Where(a => a.When == when)],
        when == AppTiming.BeforeGame ? game.AppsWaitForUsbDeviceId : null,
        when == AppTiming.BeforeGame ? game.AppsWaitForUsbDeviceName : null);

    /// <summary>
    /// Everything a game started, turned into actions that end it again – what a game's exit runs. In reverse order,
    /// so a dashboard goes down before the wheelbase software it talks to.
    /// </summary>
    public static AppPlan StopWhatWasStarted(GameEntry game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return new AppPlan(
            game.Name,
            [.. game.Apps
                .Where(a => a.Kind == AppActionKind.Start)
                .Reverse()
                .Select(a => a with { Kind = AppActionKind.Stop, WaitSeconds = 0 })],
            null,
            null);
    }
}
