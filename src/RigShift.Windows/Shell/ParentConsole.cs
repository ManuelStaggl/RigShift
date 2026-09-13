using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;

namespace RigShift.Windows.Shell;

/// <summary>
/// Text output for the command line. RigShift.exe is a GUI-subsystem program: it has no console of its own, so output
/// goes to a redirected stdout (<c>RigShift.exe list &gt; file</c>, pipes, Stream Deck) or to the console of the
/// calling shell. The shell does not wait for a GUI program, so its prompt may appear before the output.
/// </summary>
public static class ParentConsole
{
    /// <summary>Must run before anything touches <see cref="Console"/>, which caches its streams.</summary>
    public static void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        HANDLE stdout = PInvoke.GetStdHandle(STD_HANDLE.STD_OUTPUT_HANDLE);
        bool redirected = (nint)stdout != 0 && (nint)stdout != -1;
        if (!redirected && !PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS))
        {
            return; // Started from Explorer or a shortcut: nobody to tell.
        }

        // In an interactive console the prompt was already printed; start on a fresh line.
        Console.Out.Write(redirected ? text + Environment.NewLine : Environment.NewLine + text + Environment.NewLine);
        Console.Out.Flush();
    }
}
