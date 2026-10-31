using Avalonia.Controls;
using Avalonia.Interactivity;
using RigShift.App.ViewModels;
using RigShift.Avalonia.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Avalonia.Views;

/// <summary>
/// Captures where the helper windows sit. Everything open is listed and the user ticks what belongs to this game –
/// capturing the whole desktop would drag Explorer and the browser into the layout as well.
/// </summary>
public sealed partial class WindowCaptureWindow : Window
{
    private WindowLayout? _captured;

    public WindowCaptureWindow()
    {
        InitializeComponent();
        Opened += (_, _) => List.Focus();
    }

    /// <param name="current">What the entry has now; the same programs start ticked again.</param>
    /// <returns>The captured layout, or <c>null</c> if cancelled.</returns>
    internal static async Task<WindowLayout?> CaptureAsync(IReadOnlyList<OpenWindow> open, WindowLayout? current)
    {
        var window = new WindowCaptureWindow { DataContext = new WindowCaptureViewModel(open, current) };
        await AskDialog.ShowModalAsync(window);
        return window._captured;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        _captured = ((WindowCaptureViewModel)DataContext!).ToLayout();
        Log.Information("{Count} window positions captured", _captured.Windows.Count);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
