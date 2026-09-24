using System.Collections.ObjectModel;
using System.IO;
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
    public AppEditItem(AppAction action, bool showWhen = false)
    {
        ArgumentNullException.ThrowIfNull(action);

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
    }

    /// <summary>What the seconds become when "until its window is open" is ticked with none: a limit that is visible.</summary>
    internal const int DefaultWindowWaitSeconds = 60;

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
    [NotifyPropertyChangedFor(nameof(IsStart), nameof(PathNote), nameof(HasPathNote))]
    public partial Choice? SelectedKind { get; set; }

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
        OnPropertyChanged(nameof(PathNote));
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
    [NotifyPropertyChangedFor(nameof(Icon), nameof(DisplayName), nameof(PathNote), nameof(HasPathNote))]
    public partial string Path { get; set; }

    /// <summary>
    /// What is wrong with the path, in words, shown before it; <c>null</c> when nothing is. An empty path is the
    /// editor's own problem line, and stopping by process name needs no file.
    /// </summary>
    public string? PathNote
    {
        get
        {
            string path = Path.Trim();
            if (path.Length == 0)
            {
                return null;
            }

            if (!LaunchPath.IsFullyQualified(path))
            {
                return IsStart ? Loc.Instance["Restore_WarnNotFullPath"] : null;
            }

            return File.Exists(LaunchPath.Expand(path)) ? null : Loc.Instance["App_NotFound"];
        }
    }

    /// <summary>The program's own icon; <c>null</c> while the path is not a file with one.</summary>
    public System.Windows.Media.ImageSource? Icon => AppIcons.Load(Path);

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
