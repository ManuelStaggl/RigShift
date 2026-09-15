namespace RigShift.Core.Abstractions;

/// <summary>
/// Whether a full-screen application (a game, a presentation) is running right now. Implementation:
/// <c>ShellFullscreenCheck</c> over <c>SHQueryUserNotificationState</c>.
/// </summary>
public interface IFullscreenCheck
{
    bool IsFullscreenAppRunning();
}
