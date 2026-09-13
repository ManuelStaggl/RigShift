using Windows.Win32;

namespace RigShift.Windows.Shell;

public static class Foreground
{
    /// <summary>
    /// Lets another process (the running tray app) bring its window to the front. Only works while this process
    /// holds the foreground right, i.e. right after the user started it.
    /// </summary>
    public static void AllowAnyProcess() => PInvoke.AllowSetForegroundWindow(PInvoke.ASFW_ANY);
}
