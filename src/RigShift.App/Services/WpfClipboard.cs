namespace RigShift.App.Services;

/// <summary><see cref="ITextClipboard"/> through WPF.</summary>
internal sealed class WpfClipboard : ITextClipboard
{
    public void SetText(string text) => System.Windows.Clipboard.SetText(text);
}
