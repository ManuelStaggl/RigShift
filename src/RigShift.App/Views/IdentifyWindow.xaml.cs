using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RigShift.App.Localization;
using RigShift.Core.Profiles;
using RigShift.Windows.Ui;
using Serilog;

namespace RigShift.App.Views;

/// <summary>A large number and name, centered on one display for a few seconds, to tell monitors apart.</summary>
public partial class IdentifyWindow : Window
{
    private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(3);

    private readonly DisplayAssignment _mode;
    private readonly DispatcherTimer _timer = new() { Interval = ShowFor };

    private IdentifyWindow(int number, string name, DisplayAssignment mode)
    {
        InitializeComponent();
        NumberText.Text = number.ToString(Loc.Instance.Culture);
        NameText.Text = name;
        _mode = mode;
        ContentRendered += OnContentRendered;
        _timer.Tick += (_, _) => Close();
    }

    /// <summary>One window per active display; each closes on its own.</summary>
    public static void ShowAll(IEnumerable<(int Number, string Name, DisplayAssignment Mode)> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        foreach ((int number, string name, DisplayAssignment mode) in displays)
        {
            new IdentifyWindow(number, name, mode).Show();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        // Positioned once the size is known; invisible until then so it does not flash on the primary display.
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (!NativeWindow.CenterOnRect(hwnd, _mode.PositionX, _mode.PositionY, _mode.Width, _mode.Height))
        {
            Log.Warning("Identify window for {Display} could not be positioned", NameText.Text);
        }

        Opacity = 1;
        _timer.Start();
    }
}
