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
/// <param name="ProfileId">The profile whose apps these are; what they start is noted for <see cref="Profile.CloseAppsWhenLeaving"/>.</param>
internal sealed record AppPlan(
    string Name,
    IReadOnlyList<AppAction> Apps,
    string? WaitForUsbDeviceId,
    string? WaitForUsbDeviceName,
    Guid? ProfileId = null)
{
    /// <summary>Ends the apps of the profile that was left, before the wait for the USB device.</summary>
    public IReadOnlyList<AppAction> StopFirst { get; init; } = [];

    public static AppPlan For(Profile profile) => new(
        profile.Name, profile.Apps, profile.AppsWaitForUsbDeviceId, profile.AppsWaitForUsbDeviceName, profile.Id);

    /// <summary>
    /// Only the apps before the game wait for the USB device: once it runs, waiting for a wheelbase would hold up Crew
    /// Chief.
    /// </summary>
    public static AppPlan For(GameEntry game, AppTiming when) => new(
        game.Name,
        [.. game.Apps.Where(a => a.When == when)],
        when == AppTiming.BeforeGame ? game.AppsWaitForUsbDeviceId : null,
        when == AppTiming.BeforeGame ? game.AppsWaitForUsbDeviceName : null);

    /// <summary>
    /// Ends everything a game started, in reverse order so a dashboard goes down before the wheelbase software it talks
    /// to.
    /// </summary>
    public static AppPlan StopWhatWasStarted(GameEntry game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return new AppPlan(
            game.Name,
            [.. game.Apps
                .Where(a => a.Kind == AppActionKind.Start)
                .Reverse()
                .Select(a => a with { Kind = AppActionKind.Stop, Wait = AppWait.None, WaitSeconds = 0 })],
            null,
            null);
    }
}
