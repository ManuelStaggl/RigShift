using Microsoft.Win32;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Whether the user's session can switch displays right now (v4 finding A-04). While it is locked or not on the console,
/// Windows answers every display call with "access denied": a USB rule firing then failed, and the rule only started again
/// once the device was switched off and on.
/// </summary>
public interface ISessionWatch
{
    /// <summary>Not locked and connected to the console.</summary>
    bool IsInteractive { get; }

    /// <summary>Raised on a system thread when <see cref="IsInteractive"/> changed.</summary>
    event EventHandler? Changed;
}

/// <summary><see cref="ISessionWatch"/> on top of <see cref="SystemEvents.SessionSwitch"/>. Starts out interactive.</summary>
public sealed class SystemSessionWatch : ISessionWatch, IDisposable
{
    private readonly ILogger _log;
    private volatile bool _locked;
    private volatile bool _away;

    public SystemSessionWatch(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<SystemSessionWatch>();
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public event EventHandler? Changed;

    public bool IsInteractive => !_locked && !_away;

    public void Dispose() => SystemEvents.SessionSwitch -= OnSessionSwitch;

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        bool before = IsInteractive;
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                _locked = true;
                break;
            case SessionSwitchReason.SessionUnlock:
                _locked = false;
                break;
            case SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteConnect or SessionSwitchReason.RemoteDisconnect:
                _away = true;
                break;
            case SessionSwitchReason.ConsoleConnect:
                _away = false;
                break;
        }

        _log.Information("Session {Reason}: {State}", e.Reason, IsInteractive ? "interactive" : "not interactive");
        if (IsInteractive != before)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
