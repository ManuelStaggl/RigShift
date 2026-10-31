using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

/// <summary>The open windows with tick boxes; what is ticked becomes the game's window layout.</summary>
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

    /// <summary>"SimHubWPF · 800 × 600" under the title; the position is the tooltip, not the headline.</summary>
    public string Details => string.Create(
        Loc.Instance.Culture,
        $"{Window.ProcessName} · {Window.Bounds.Width} × {Window.Bounds.Height}");

    /// <summary>"3840, 0" – enough to tell two windows of one program apart.</summary>
    public string Position => string.Create(Loc.Instance.Culture, $"{Window.Bounds.Left}, {Window.Bounds.Top}");

    [ObservableProperty]
    public partial bool IsChosen { get; set; }
}
