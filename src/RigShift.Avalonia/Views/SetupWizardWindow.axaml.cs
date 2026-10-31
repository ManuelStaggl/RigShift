using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using RigShift.App.ViewModels;
using RigShift.Core.Profiles;
using RigShift.Windows.Ui;

namespace RigShift.Avalonia.Views;

/// <summary>Hosts the setup assistant: follows display changes and looks at the USB devices while a step needs it.</summary>
public sealed partial class SetupWizardWindow : Window
{
    /// <summary>The symbols of the three welcome cards: the desk, the rig, the USB trigger.</summary>
    public static readonly IValueConverter WelcomeIcon = new FuncValueConverter<int, Geometry>(number => Geometry.Parse(number switch
    {
        2 => Glyphs.ProfileData(ProfileIcons.Rig),
        3 => Glyphs.Triggers,
        _ => Glyphs.Display,
    }));

    private readonly DispatcherTimer _usbTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    // A topology change sends a burst of messages; react once when it has settled.
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromSeconds(1.5) };

    // One delegate instance, so the hook can be removed again.
    private readonly Win32Properties.CustomWndProcHookCallback _hook;

    public SetupWizardWindow()
    {
        InitializeComponent();
        TextScale.Apply(this);
        _hook = WndProc;
    }

    private SetupWizardViewModel ViewModel => (SetupWizardViewModel)DataContext!;

    /// <summary>Shows the assistant modal over the main window, or on its own when the window is in the tray.</summary>
    internal static async Task ShowAsync(SetupWizardViewModel viewModel)
    {
        var window = new SetupWizardWindow { DataContext = viewModel };
        viewModel.CloseRequested += window.OnCloseRequested;
        viewModel.PropertyChanged += window.OnViewModelChanged;
        window._usbTimer.Tick += (_, _) => viewModel.PollUsb();
        window._settle.Tick += window.OnDisplaysSettled;
        // Esc leaves the assistant like its close button; a control that uses Esc itself (an open list) handles it first.
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                window.Close();
            }
        };
        window.Opened += (_, _) =>
        {
            Win32Properties.AddWndProcHookCallback(window, window._hook);
            window.FollowStep();
            if (viewModel.IsWelcome)
            {
                window.StartButton.Focus();
            }
        };
        window.Closed += (_, _) =>
        {
            window._usbTimer.Stop();
            window._settle.Stop();
            Win32Properties.RemoveWndProcHookCallback(window, window._hook);
            viewModel.CloseRequested -= window.OnCloseRequested;
            viewModel.PropertyChanged -= window.OnViewModelChanged;
        };

        if (App.Owner is { } owner)
        {
            await window.ShowDialog(owner);
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await closed.Task;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeWindow.WmDisplayChange)
        {
            _settle.Stop();
            _settle.Start();
        }

        return 0;
    }

    private async void OnDisplaysSettled(object? sender, EventArgs e)
    {
        _settle.Stop();
        await ViewModel.RefreshDisplaysAsync();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SetupWizardViewModel.Step))
        {
            return;
        }

        FollowStep();

        // Keyboard users land in the name field; the heading is a live region, so screen readers announce every step.
        if (ViewModel.IsProfileStep)
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }
    }

    /// <summary>The USB devices are looked at only while the trigger step is open.</summary>
    private void FollowStep()
    {
        if (ViewModel.IsTrigger)
        {
            _usbTimer.Start();
        }
        else
        {
            _usbTimer.Stop();
        }
    }
}
