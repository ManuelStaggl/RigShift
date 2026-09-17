using System.ComponentModel;
using System.Windows.Threading;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>Hosts the setup assistant: follows display changes and looks at the USB devices while a step needs it.</summary>
public partial class SetupWizardWindow : FluentWindow
{
    private readonly SetupWizardViewModel _viewModel;
    private readonly DisplayChangeWatcher _watcher;
    private readonly DispatcherTimer _usbTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public SetupWizardWindow(SetupWizardViewModel viewModel, DisplayChangeWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(watcher);
        _viewModel = viewModel;
        _watcher = watcher;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.CloseRequested += (_, _) => Close();
        viewModel.PropertyChanged += OnViewModelChanged;
        watcher.DisplaysChanged += OnDisplaysChanged;
        _usbTimer.Tick += (_, _) => viewModel.PollUsb();
        Loaded += (_, _) => StartButton.Focus();
        Closed += (_, _) =>
        {
            _usbTimer.Stop();
            watcher.DisplaysChanged -= OnDisplaysChanged;
            viewModel.PropertyChanged -= OnViewModelChanged;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        BrandWindow.ApplyChrome(this);
    }

    private async void OnDisplaysChanged(object? sender, EventArgs e) => await _viewModel.RefreshDisplaysAsync();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SetupWizardViewModel.Step))
        {
            return;
        }

        if (_viewModel.IsTrigger)
        {
            _usbTimer.Start();
        }
        else
        {
            _usbTimer.Stop();
        }

        // Keyboard users land in the name field; the heading is a live region, so screen readers announce every step.
        if (_viewModel.IsProfileStep)
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }
    }
}
