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

    public static AppPlan For(GameEntry game) => new(
        game.Name, game.Apps, game.AppsWaitForUsbDeviceId, game.AppsWaitForUsbDeviceName);

    /// <summary>The same apps, but only the ones to end – what a game's exit runs.</summary>
    public AppPlan OnlyStopActions() => this with
    {
        Apps = [.. Apps.Where(a => a.Kind == AppActionKind.Start).Select(a => a with { Kind = AppActionKind.Stop, WaitSeconds = 0 })],
        WaitForUsbDeviceId = null,
        WaitForUsbDeviceName = null,
    };
}
