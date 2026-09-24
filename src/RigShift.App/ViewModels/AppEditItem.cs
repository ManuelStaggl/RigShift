using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

/// <summary>One app entry in the editor.</summary>
public sealed partial class AppEditItem : ObservableObject
{
    /// <param name="showWhen">
    /// Offer "before / after the game". Only the game editor does: a profile has no game to be before or after.
    /// </param>
    /// <param name="time">The clock of the pause before the file is looked for; tests pass their own.</param>
    public AppEditItem(AppAction action, bool showWhen = false, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        _time = time ?? TimeProvider.System;
        ShowWhen = showWhen;
        FillKindChoices();
        FillWhenChoices();
        SelectedKind = KindChoices[action.Kind == AppActionKind.Stop ? 1 : 0];
        SelectedWhen = WhenChoices[action.When == AppTiming.AfterGame ? 1 : 0];
        Path = action.Path;
        _pickedPath = action.Name is null ? null : action.Path;
        _pickedName = action.Name;
        Arguments = action.Arguments ?? string.Empty;
        WaitSeconds = action.WaitSeconds;
        WaitForWindow = action.WaitForWindow;
        _ready = true;
        CheckPath(pause: false);
    }

    /// <summary>What the seconds become when "until its window is open" is ticked with none: a limit that is visible.</summary>
    internal const int DefaultWindowWaitSeconds = 60;

    /// <summary>How long typing has to pause before the file is looked for.</summary>
    private static readonly TimeSpan PathCheckPause = TimeSpan.FromMilliseconds(300);

    private readonly TimeProvider _time;
    private readonly bool _ready;
    private CancellationTokenSource? _pathCheck;
    private string? _pickedPath;
    private string? _pickedName;

    /// <summary>Takes path and display name from the picker; the name only survives as long as the path stays the picked one.</summary>
    internal void SetPicked(string path, string? name)
    {
        Path = path;
        _pickedPath = name is null ? null : path;
        _pickedName = name;
        OnPropertyChanged(nameof(DisplayName));
    }

    /// <summary>The picked name, else the file name: the row's first line.</summary>
    public string DisplayName =>
        _pickedName is not null && string.Equals(Path.Trim(), _pickedPath, StringComparison.OrdinalIgnoreCase)
            ? _pickedName
            : System.IO.Path.GetFileNameWithoutExtension(Path) is { Length: > 0 } file ? file : Loc.Instance["App_Path"];

    public ObservableCollection<Choice> KindChoices { get; } = [];

    /// <summary>Null only for a moment while the list is rebuilt; that counts as "start".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStart))]
    public partial Choice? SelectedKind { get; set; }

    partial void OnSelectedKindChanged(Choice? value) => CheckPath(pause: false);

    /// <summary>Arguments only apply when starting.</summary>
    public bool IsStart => SelectedKind?.Key != nameof(AppActionKind.Stop);

    public ObservableCollection<Choice> WhenChoices { get; } = [];

    /// <summary>Only shown in the game editor; a profile ignores it.</summary>
    public bool ShowWhen { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAfterGame))]
    public partial Choice? SelectedWhen { get; set; }

    public bool IsAfterGame => SelectedWhen?.Key == nameof(AppTiming.AfterGame);

    /// <summary>New texts after a language change, same selection.</summary>
    internal void Relabel()
    {
        bool start = IsStart;
        bool after = IsAfterGame;
        FillKindChoices();
        FillWhenChoices();
        SelectedKind = KindChoices[start ? 0 : 1];
        SelectedWhen = WhenChoices[after ? 1 : 0];
        CheckPath(pause: false);
    }

    public bool HasPathNote => PathNote is not null;

    private void FillKindChoices()
    {
        KindChoices.Clear();
        KindChoices.Add(new Choice(nameof(AppActionKind.Start), Loc.Instance["App_Start"]));
        KindChoices.Add(new Choice(nameof(AppActionKind.Stop), Loc.Instance["App_Stop"]));
    }

    private void FillWhenChoices()
    {
        WhenChoices.Clear();
        WhenChoices.Add(new Choice(nameof(AppTiming.BeforeGame), Loc.Instance["App_BeforeGame"]));
        WhenChoices.Add(new Choice(nameof(AppTiming.AfterGame), Loc.Instance["App_AfterGame"]));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string Path { get; set; }

    partial void OnPathChanged(string value) => CheckPath(pause: true);

    /// <summary>
    /// What is wrong with the path, in words, shown before it; <c>null</c> when nothing is. An empty path is the
    /// editor's own problem line, and stopping by process name needs no file.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPathNote))]
    public partial string? PathNote { get; private set; }

    /// <summary>The program's own icon; <c>null</c> while the path is not a file with one.</summary>
    [ObservableProperty]
    public partial ImageSource? Icon { get; private set; }

    /// <summary>The last look at the file, done or cancelled; tests wait for it.</summary>
    internal Task PathChecked { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Note and icon follow the path. Whether the file exists is asked in the background once typing pauses: every key
    /// used to ask the disk twice on the UI thread, and a path on a sleeping NAS froze the editor for the network's
    /// timeout (v4 finding A-12). A network path is not asked at all – whether the share is up right now says nothing
    /// about the switch later.
    /// </summary>
    private void CheckPath(bool pause)
    {
        if (!_ready)
        {
            return;
        }

        _pathCheck?.Cancel();
        _pathCheck = null;
        string path = Path.Trim();
        if (path.Length == 0 || LaunchPath.IsNetwork(path))
        {
            Show(null, null);
            return;
        }

        if (!LaunchPath.IsFullyQualified(path))
        {
            Show(IsStart ? Loc.Instance["Restore_WarnNotFullPath"] : null, null);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _pathCheck = cancellation;
        PathChecked = LookAsync(LaunchPath.Expand(path), pause, cancellation.Token);
    }

    private async Task LookAsync(string file, bool pause, CancellationToken cancellationToken)
    {
        try
        {
            if (pause)
            {
                await Task.Delay(PathCheckPause, _time, cancellationToken);
            }

            (bool exists, ImageSource? icon) = await Task.Run(
                () => File.Exists(file) ? (true, AppIcons.Load(file)) : (false, null), cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                Show(exists ? null : Loc.Instance["App_NotFound"], icon);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer path is being looked at.
        }
    }

    private void Show(string? note, ImageSource? icon)
    {
        PathNote = note;
        Icon = icon;
    }

    [ObservableProperty]
    public partial string Arguments { get; set; }

    [ObservableProperty]
    public partial double? WaitSeconds { get; set; }

    /// <summary>The wait ends once the program shows a window; the seconds are the longest wait.</summary>
    [ObservableProperty]
    public partial bool WaitForWindow { get; set; }

    partial void OnWaitForWindowChanged(bool value)
    {
        if (value && (WaitSeconds ?? 0) <= 0)
        {
            WaitSeconds = DefaultWindowWaitSeconds;
        }
    }

    public AppAction ToAction() => new()
    {
        Kind = IsStart ? AppActionKind.Start : AppActionKind.Stop,
        Path = Path.Trim(),
        Name = _pickedName is not null && string.Equals(Path.Trim(), _pickedPath, StringComparison.OrdinalIgnoreCase) ? _pickedName : null,
        Arguments = IsStart && !string.IsNullOrWhiteSpace(Arguments) ? Arguments.Trim() : null,
        WaitSeconds = (int)Math.Clamp(Math.Round(WaitSeconds ?? 0), 0, 300),
        WaitForWindow = IsStart && WaitForWindow,
        When = IsAfterGame ? AppTiming.AfterGame : AppTiming.BeforeGame,
    };
}
