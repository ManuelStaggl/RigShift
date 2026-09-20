using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>
/// Captures where the helper windows sit. Everything open is listed and the user ticks what belongs to this game –
/// capturing the whole desktop would drag Explorer and the browser into the layout as well.
/// </summary>
public partial class WindowCaptureWindow : FluentWindow
{
    private readonly WindowCaptureViewModel _viewModel;

    private WindowCaptureWindow(WindowCaptureViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        TextScale.Apply(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        BrandWindow.ApplyChrome(this);
    }

    public WindowLayout? Captured { get; private set; }

    /// <returns>The captured layout, or <c>null</c> if cancelled.</returns>
    public static WindowLayout? Capture(Window? owner, GameDialogs dialogs, WindowLayout? current)
    {
        ArgumentNullException.ThrowIfNull(dialogs);

        var viewModel = new WindowCaptureViewModel(dialogs.OpenWindows(), current);
        var window = new WindowCaptureWindow(viewModel);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return window.ShowDialog() == true ? window.Captured : null;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Captured = _viewModel.ToLayout();
        DialogResult = true;
    }
}

public sealed partial class WindowCaptureViewModel : ObservableObject
{
    /// <param name="current">A layout that was captured before, so the same windows start ticked again.</param>
    public WindowCaptureViewModel(IReadOnlyList<OpenWindow> open, WindowLayout? current)
    {
        ArgumentNullException.ThrowIfNull(open);

        // RigShift's own windows are never part of a game's layout.
        string self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        foreach (OpenWindow window in open.Where(w => !string.Equals(w.ProcessName, self, StringComparison.OrdinalIgnoreCase)))
        {
            bool chosen = current?.Windows.Any(w =>
                string.Equals(w.ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase)) ?? false;
            var row = new CaptureRow(window) { IsChosen = chosen };
            row.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(ChosenText));
                OnPropertyChanged(nameof(CanSave));
            };
            Windows.Add(row);
        }
    }

    public ObservableCollection<CaptureRow> Windows { get; } = [];

    public string ChosenText => Loc.Format("Capture_Chosen", Windows.Count(w => w.IsChosen));

    /// <summary>Saving nothing would only clear the positions, and the editor has a button for that.</summary>
    public bool CanSave => Windows.Any(w => w.IsChosen);

    public WindowLayout ToLayout() => new()
    {
        CapturedAt = DateTimeOffset.Now,
        Windows =
        [
            .. Windows.Where(w => w.IsChosen).Select(w => new WindowPlacement
            {
                ProcessName = w.Window.ProcessName,
                Title = string.IsNullOrWhiteSpace(w.Window.Title) ? null : w.Window.Title,
                Bounds = w.Window.Bounds,
                State = w.Window.State,
            }),
        ],
    };
}

/// <summary>One open window in the capture list.</summary>
public sealed partial class CaptureRow(OpenWindow window) : ObservableObject
{
    public OpenWindow Window { get; } = window;

    public string Title => string.IsNullOrWhiteSpace(Window.Title) ? Window.ProcessName : Window.Title;

    /// <summary>"SimHubWPF · 800 × 600" under the title (D-02); the position is the tooltip, not the headline.</summary>
    public string Details => string.Create(
        Loc.Instance.Culture,
        $"{Window.ProcessName} · {Window.Bounds.Width} × {Window.Bounds.Height}");

    /// <summary>"3840, 0" – enough to tell two windows of one program apart.</summary>
    public string Position => string.Create(Loc.Instance.Culture, $"{Window.Bounds.Left}, {Window.Bounds.Top}");

    [ObservableProperty]
    public partial bool IsChosen { get; set; }
}
