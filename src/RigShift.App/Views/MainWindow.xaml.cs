using System.ComponentModel;
using RigShift.App.Services;
using RigShift.App.Views.Pages;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>Main window. Closing only hides it – RigShift keeps running in the tray.</summary>
public partial class MainWindow : FluentWindow
{
    private readonly IAppShell _shell;
    private Type _page = typeof(ProfilesPage);

    public MainWindow(IServiceProvider services, IAppShell shell)
    {
        _shell = shell;
        InitializeComponent();
        RootNavigation.SetServiceProvider(services);
        Loaded += (_, _) =>
        {
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: false);
            RootNavigation.Navigate(_page);
        };
    }

    public void ShowPage(Type? page)
    {
        _page = page ?? _page;
        if (IsLoaded)
        {
            RootNavigation.Navigate(_page);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_shell.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
