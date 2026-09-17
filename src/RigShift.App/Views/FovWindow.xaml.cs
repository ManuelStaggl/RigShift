using System.IO;
using System.Windows;
using RigShift.App.ViewModels;
using Serilog;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>The field-of-view dialog. Its inputs are remembered when it closes.</summary>
public partial class FovWindow : FluentWindow
{
    private readonly FovViewModel _viewModel;
    private readonly ILogger _log;

    private FovWindow(FovViewModel viewModel, ILogger log)
    {
        _viewModel = viewModel;
        _log = log.ForContext<FovWindow>();
        DataContext = viewModel;
        InitializeComponent();
    }

    public static void Show(Window? owner, FovViewModel viewModel, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var window = new FovWindow(viewModel, log);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        BrandWindow.ApplyChrome(this, "RigShift.Brush.Page");
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            await _viewModel.SaveInputsAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "FOV inputs could not be saved");
        }
    }
}
