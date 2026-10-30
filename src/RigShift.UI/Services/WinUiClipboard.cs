using RigShift.App.Services;
using Windows.ApplicationModel.DataTransfer;

namespace RigShift.UI.Services;

internal sealed class WinUiClipboard : ITextClipboard
{
    public void SetText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
