using System.Runtime.InteropServices;
using RigShift.Core.Abstractions;
using Serilog;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace RigShift.Windows.Shell;

/// <summary>
/// Asks the shell whether a full-screen application is running – the same check Windows uses to hold back
/// notifications. Covers exclusive Direct3D games, borderless windows that cover a screen and presentation mode.
/// </summary>
public sealed class ShellFullscreenCheck(ILogger log) : IFullscreenCheck
{
    private readonly ILogger _log = log.ForContext<ShellFullscreenCheck>();

    public bool IsFullscreenAppRunning()
    {
        try
        {
            PInvoke.SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state).ThrowOnFailure();
            return state is QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY
                or QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE
                or QUERY_USER_NOTIFICATION_STATE.QUNS_APP;
        }
        catch (COMException ex)
        {
            // Unknown state: never hold an end action back on a guess.
            _log.Warning(ex, "Full-screen state could not be read");
            return false;
        }
    }
}
