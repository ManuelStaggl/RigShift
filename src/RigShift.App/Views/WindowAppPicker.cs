using System.Windows;
using RigShift.App.ViewModels;

namespace RigShift.App.Views;

/// <summary>The app picker window, in front of the window the user works in.</summary>
public sealed class WindowAppPicker : IAppPicker
{
    public PickedApp? Pick(string? currentPath) => AppPickerWindow.Pick(ActiveWindow(), currentPath);

    private static Window? ActiveWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
}
