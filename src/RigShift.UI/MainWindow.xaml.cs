using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Windows.Ui;
using Serilog;

namespace RigShift.UI;

public sealed partial class MainWindow : Window
{
    /// <summary>The intended size in device-independent units, as the WPF window had it.</summary>
    private const double IntendedWidth = 1280;
    private const double IntendedHeight = 780;

    private Shell _shell;
    private bool _placed;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/rigshift.ico");
        _shell = new Shell();
        ShellHost.Content = _shell;

        // Texts are read once per load ({l:Tr}); a new language takes a new shell on the same page. Loc raises this on
        // the UI thread.
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            AppPage page = _shell.Page;
            _shell = new Shell();
            ShellHost.Content = _shell;
            _shell.Navigate(page);
        };
    }

    /// <summary>Brings the window forward, restored, on <paramref name="page"/> (or the page it showed last).</summary>
    public void Show(AppPage? page)
    {
        _shell.Navigate(page ?? _shell.Page);
        if (!_placed)
        {
            _placed = true;
            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (NativeWindow.OpenOnCursorMonitor(handle, IntendedWidth, IntendedHeight) is { } placed)
            {
                Log.Information("Main window opened at {Bounds}{Primary}", placed.Bounds, placed.ToPrimary ? " on the primary monitor" : string.Empty);
            }
        }

        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
    }
}
