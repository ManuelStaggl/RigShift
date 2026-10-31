using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using RigShift.App.Services;
using Serilog;

namespace RigShift.Avalonia.Services;

internal sealed class AvaloniaClipboard : ITextClipboard
{
    public void SetText(string text)
    {
        IClipboard? clipboard = (global::Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .Select(w => TopLevel.GetTopLevel(w)?.Clipboard)
            .FirstOrDefault(c => c is not null);
        if (clipboard is null)
        {
            Log.Warning("No window open, nothing copied to the clipboard");
            return;
        }

        _ = clipboard.SetTextAsync(text);
    }
}
