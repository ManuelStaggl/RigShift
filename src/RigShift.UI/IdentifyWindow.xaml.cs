using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RigShift.App.Localization;
using RigShift.Core.Profiles;
using RigShift.Windows.Ui;
using Serilog;
using Windows.Graphics;

namespace RigShift.UI;

/// <summary>A large number and name, centered on one display for a few seconds, to tell monitors apart.</summary>
public sealed partial class IdentifyWindow : Window
{
    private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(3);

    private IdentifyWindow(int number, string name)
    {
        InitializeComponent();
        NumberText.Text = number.ToString(Loc.Instance.Culture);
        NameText.Text = name;
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }
    }

    /// <summary>One window per active display; each closes on its own.</summary>
    public static void ShowAll(IEnumerable<(int Number, string Name, DisplayAssignment Mode)> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        foreach ((int number, string name, DisplayAssignment mode) in displays)
        {
            new IdentifyWindow(number, name).ShowOn(mode);
        }
    }

    private void ShowOn(DisplayAssignment mode)
    {
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // Moved onto the display first: its DPI decides the size in pixels.
        NativeWindow.CenterOnRect(handle, mode.PositionX, mode.PositionY, mode.Width, mode.Height);
        double scale = NativeWindow.DpiScale(handle);
        AppWindow.Resize(new SizeInt32((int)(320 * scale), (int)(220 * scale)));
        if (!NativeWindow.CenterOnRect(handle, mode.PositionX, mode.PositionY, mode.Width, mode.Height))
        {
            Log.Warning("Identify window for {Display} could not be positioned", NameText.Text);
        }

        // Shown without taking the focus from the RigShift window.
        AppWindow.Show(activateWindow: false);
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = ShowFor;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Close();
        timer.Start();
    }
}
