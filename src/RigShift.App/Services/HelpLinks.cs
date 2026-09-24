using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// The guides behind a warning's "Help" or "How to fix" (v4 finding U-11): a warning names the next step, the guide
/// explains it. Commands, so any template can reach them with <c>x:Static</c>.
/// </summary>
public static class HelpLinks
{
    /// <summary>Monitors in standby or behind a power strip, and what RigShift does about them.</summary>
    public static IRelayCommand MonitorStandby { get; } = new RelayCommand(() => Open("docs/monitor-standby.md"));

    /// <summary>USB selective suspend: why pedals drop out and how to turn it off for one device.</summary>
    public static IRelayCommand UsbPowerSaving { get; } = new RelayCommand(() => Open("docs/usb-power-saving.md"));

    private static void Open(string document) =>
        ShellFolders.OpenUrl($"{UpdateService.RepositoryUrl}/blob/main/{document}", Log.Logger);
}
