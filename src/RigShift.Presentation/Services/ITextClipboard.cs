using System.Runtime.InteropServices;

namespace RigShift.App.Services;

/// <summary>Text to the Windows clipboard, through the running UI framework.</summary>
public interface ITextClipboard
{
    /// <exception cref="COMException">Another program holds the clipboard.</exception>
    void SetText(string text);
}
