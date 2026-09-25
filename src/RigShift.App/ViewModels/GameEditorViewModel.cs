using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Controls;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// The game detail's editor: the tabs in the order a session runs them (game, profile, tools, windows, end), live
/// validation with a problem count, and the dirty flag for the save bar. Nothing is written until <see cref="SaveAsync"/>.
/// </summary>
public sealed partial class GameEditorViewModel : ObservableObject, IDetailEditor, IHotkeyField
{
    private readonly GameEntry _original;
    private readonly IReadOnlyList<Profile> _profiles;
    private readonly IReadOnlyList<GameEntry> _games;
    private readonly GameCatalog _catalog;
    private readonly HotkeyRecorder _hotkeyRecorder;
    private readonly ILogger _log;
    private GameEntry _initial;
    private GameLaunch _launch;
    private bool _loading = true;

    public GameEditorViewModel(GameEntry game, bool isNew, GameEditorContext context, GameEditorServices services)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);

        _original = game;
        _initial = game;
        Saved = game;
        _profiles = context.Profiles;
        _games = context.Games;
        _catalog = services.Catalog;
        _hotkeyRecorder = new HotkeyRecorder(services.Hotkeys, HotkeyUseKind.Game, game.Id, "Game_HotkeyHint");
        _log = services.Log.ForContext<GameEditorViewModel>();
        _launch = game.Launch;
        IsNew = isNew;

        Name = game.Name;
        LaunchTarget = game.Launch.Target;
        ProcessName = game.Launch.ProcessName ?? string.Empty;
        LauncherProcessName = game.LauncherProcessName ?? string.Empty;
        StartWithGame = game.StartWithGame;
        StopApps = game.Exit.StopApps;
        WindowLayout = game.WindowLayout;
        Hotkey = game.Hotkey;

        FillChoices();
        SelectedProfile = ProfileOptions.FirstOrDefault(c => c.Key == game.ProfileId?.ToString()) ?? NoProfileOption;
        SelectedEnd = EndChoices.First(c => c.Key == game.EndsWith.ToString());
        SelectedExit = ExitChoices.FirstOrDefault(c => c.Key == ExitKeyOf(game.Exit)) ?? ExitChoices[0];
        SelectedIcon = IconChoices.FirstOrDefault(c => c.Key == ProfileIcons.Normalize(game.Icon)) ?? IconChoices[0];

        AppList = new AppListEditor(game.Apps, showWhen: true, context.AppsWaitDevice, services.AppPicker, "Game_AddTool");
        AppList.Changed += OnPartChanged;
        Loc.Instance.PropertyChanged += OnLanguageChanged;
        _loading = false;
        Recalculate();
    }

    public Guid Id => _original.Id;

    /// <summary>The game as it is on disk: as loaded, then as last saved. The page compares it with the catalog.</summary>
    public GameEntry Saved { get; private set; }

    /// <summary>The name before the last save, when that save renamed the game.</summary>
    public string? RenamedFrom { get; private set; }

    /// <summary>Not saved yet: the save bar stays until the first save, and playing is not possible.</summary>
    [ObservableProperty]
    public partial bool IsNew { get; private set; }

    /// <summary>Anything differs from the game on disk, or the game is not on disk yet.</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    /// <summary>Validation problems as they stand; the save bar disables Save while there are any.</summary>
    [ObservableProperty]
    public partial int ProblemCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNameProblem))]
    public partial string? NameProblem { get; private set; }

    public bool HasNameProblem => NameProblem is not null;

    /// <summary>Nothing chosen to start – the tab "Game" carries the dot.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaunchProblem), nameof(HasGameTabProblem))]
    public partial string? LaunchProblem { get; private set; }

    public bool HasLaunchProblem => LaunchProblem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHotkeyProblem), nameof(HasGameTabProblem))]
    public partial string? HotkeyProblem { get; private set; }

    public bool HasHotkeyProblem => HotkeyProblem is not null;

    /// <summary>The tab "Game" holds launch, process and hotkey; one dot for all of them.</summary>
    public bool HasGameTabProblem => HasLaunchProblem || HasHotkeyProblem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppsProblem))]
    public partial string? AppsProblem { get; private set; }

    public bool HasAppsProblem => AppsProblem is not null;

    /// <summary>A save that failed on disk; cleared by the next successful save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>
    /// The last session found no game process: the process field is the place to fix it, so it says so there
    /// (F5, error row). Set by the page from the session result; cleared once the field changes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProcessHint), nameof(ProcessHintIsError))]
    public partial bool ProcessNotRecognised { get; set; }

    public string ProcessHint => ProcessNotRecognised ? Loc.Instance["Game_ProcessNotRecognised"] : Loc.Instance["Game_ProcessNameHint"];

    public bool ProcessHintIsError => ProcessNotRecognised;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandText))]
    public partial string Name { get; set; }

    /// <summary>What the game is started with: a program path, or the store's id.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchText), nameof(HasLaunch))]
    public partial string LaunchTarget { get; set; }

    /// <summary>"Steam", "Epic", "Xbox" or "EXE": the chip in front of the launch text.</summary>
    public string LaunchKindText => _launch.Kind switch
    {
        GameLaunchKind.Steam => "Steam",
        GameLaunchKind.Epic => "Epic",
        GameLaunchKind.Xbox => "Xbox",
        _ => "EXE",
    };

    /// <summary>The install folder of a store game (its id says nothing to anyone), else the program path.</summary>
    public string LaunchText => _launch.Kind == GameLaunchKind.Executable
        ? LaunchTarget
        : _launch.InstallFolder is { Length: > 0 } folder ? folder : LaunchTarget;

    public bool HasLaunch => !string.IsNullOrWhiteSpace(LaunchTarget);

    /// <summary>
    /// The game's own process, without extension. Visible and editable on purpose: a first start learns it, but for a
    /// sim whose interface stays open the guess can land on the interface, and then the user has to be able to say so.
    /// </summary>
    [ObservableProperty]
    public partial string ProcessName { get; set; }

    /// <summary>The launcher or interface in front of the game, for <see cref="SessionEnd.LauncherProcess"/>.</summary>
    [ObservableProperty]
    public partial string LauncherProcessName { get; set; }

    [ObservableProperty]
    public partial bool StartWithGame { get; set; }

    [ObservableProperty]
    public partial bool StopApps { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowsText), nameof(HasWindows), nameof(SavedWindows))]
    public partial WindowLayout? WindowLayout { get; set; }

    /// <summary>The captured windows, one line each (G-05).</summary>
    public IReadOnlyList<SavedWindowRow> SavedWindows => WindowLayout is { IsEmpty: false } layout
        ? [.. layout.Windows.Select(w => new SavedWindowRow(w))]
        : [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyText), nameof(HasHotkey))]
    public partial Hotkey? Hotkey { get; set; }

    public string HotkeyText => Hotkey is null ? string.Empty : HotkeyFormat.Format(Hotkey);

    public bool HasHotkey => Hotkey is not null;

    /// <summary>The line under the hotkey field: how to record one, or why the last combination was refused.</summary>
    public string HotkeyHint => _hotkeyRecorder.Hint;

    /// <summary>Every profile with its picture, then "Don't switch anything" last (tab "Profile").</summary>
    public ObservableCollection<ProfileOption> ProfileOptions { get; } = [];

    [ObservableProperty]
    public partial ProfileOption? SelectedProfile { get; set; }

    private ProfileOption NoProfileOption => ProfileOptions.First(o => o.Key is null);

    public ObservableCollection<Choice> EndChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsLauncherProcess))]
    public partial Choice? SelectedEnd { get; set; }

    public bool NeedsLauncherProcess => SelectedEnd?.Key == nameof(SessionEnd.LauncherProcess);

    public ObservableCollection<Choice> ExitChoices { get; } = [];

    [ObservableProperty]
    public partial Choice? SelectedExit { get; set; }

    public ObservableCollection<Choice> IconChoices { get; } = [];

    /// <summary>A symbol the user picked; "none" shows the game's own icon.</summary>
    [ObservableProperty]
    public partial Choice? SelectedIcon { get; set; }

    /// <summary>The tools the session starts and ends, and the USB device they wait for.</summary>
    public AppListEditor AppList { get; }

    public string WindowsText => WindowLayout is { IsEmpty: false } layout
        ? Loc.Format(
            layout.Windows.Count == 1 ? "Game_WindowsSavedAtOne" : "Game_WindowsSavedAt",
            layout.Windows.Count,
            layout.CapturedAt.ToLocalTime().ToString("g", Loc.Instance.Culture))
        : Loc.Instance["Game_WindowsNone"];

    public bool HasWindows => WindowLayout is { IsEmpty: false };

    /// <summary>The URL that starts this game's session, for Stream Deck and shortcuts.</summary>
    public string CommandText => "rigshift://play/" + Uri.EscapeDataString(Name.Trim());

    /// <summary>Recording a hotkey RigShift holds would fire it, so they rest while the field has the focus.</summary>
    public void BeginHotkeyRecording() => _hotkeyRecorder.Begin();

    public void EndHotkeyRecording() => _hotkeyRecorder.End();

    /// <summary>Takes a game the user picked from the installed ones, with everything the template knows.</summary>
    public void SetLaunch(InstalledGame installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        _launch = installed.Launch;
        if (string.IsNullOrWhiteSpace(Name) || Name == Loc.Instance["Games_NewName"])
        {
            Name = installed.Name;
        }

        GameEntry filled = SimTemplates.Apply(new GameEntry { Id = _original.Id, Name = installed.Name, Launch = installed.Launch });
        ProcessName = filled.Launch.ProcessName ?? string.Empty;
        LauncherProcessName = filled.LauncherProcessName ?? string.Empty;
        SelectedEnd = EndChoices.First(c => c.Key == filled.EndsWith.ToString());
        LaunchTarget = installed.Launch.Target;
        OnPropertyChanged(nameof(LaunchKindText));
        OnPropertyChanged(nameof(LaunchText));
    }

    /// <summary>Takes a program the user browsed for.</summary>
    public void SetExecutable(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _launch = new GameLaunch { Kind = GameLaunchKind.Executable, Target = path };
        ProcessName = string.Empty;
        if (string.IsNullOrWhiteSpace(Name) || Name == Loc.Instance["Games_NewName"])
        {
            Name = Path.GetFileNameWithoutExtension(path);
        }

        LaunchTarget = path;
        OnPropertyChanged(nameof(LaunchKindText));
        OnPropertyChanged(nameof(LaunchText));
    }

    /// <summary>A key combination pressed in the hotkey field; without Ctrl, Alt or Win it only shows a hint.</summary>
    public void RecordHotkey(HotkeyModifiers modifiers, int virtualKey)
    {
        if (_hotkeyRecorder.Record(modifiers, virtualKey) is { } hotkey)
        {
            Hotkey = hotkey;
            _log.Information("Game editor recorded hotkey {Hotkey}", HotkeyText);
        }

        OnPropertyChanged(nameof(HotkeyHint));
    }

    [RelayCommand]
    public void ClearHotkey()
    {
        Hotkey = null;
        _hotkeyRecorder.Reset();
        OnPropertyChanged(nameof(HotkeyHint));
    }

    [RelayCommand]
    private void ChooseIcon(Choice? choice)
    {
        if (choice is not null)
        {
            SelectedIcon = choice;
        }
    }

    [RelayCommand]
    private void ClearWindows() => WindowLayout = null;

    /// <summary>The entry as it would be saved.</summary>
    public GameEntry ToGame() => _original with
    {
        Name = Name.Trim(),
        Icon = SelectedIcon?.Key,
        Launch = _launch with
        {
            Target = LaunchTarget.Trim(),
            ProcessName = string.IsNullOrWhiteSpace(ProcessName) ? null : ProcessName.Trim(),
        },
        ProfileId = Guid.TryParse(SelectedProfile?.Key, out Guid profileId) ? profileId : null,
        Apps = AppList.Build(),
        AppsWaitForUsbDeviceId = AppList.WaitDevice.DeviceId,
        AppsWaitForUsbDeviceName = AppList.WaitDevice.DeviceName,
        WindowLayout = WindowLayout is { IsEmpty: false } ? WindowLayout : null,
        EndsWith = SelectedEnd?.Key == nameof(SessionEnd.LauncherProcess) ? SessionEnd.LauncherProcess : SessionEnd.GameProcess,
        LauncherProcessName = string.IsNullOrWhiteSpace(LauncherProcessName) ? null : LauncherProcessName.Trim(),
        Exit = ExitFromChoice(),
        Hotkey = Hotkey,
        StartWithGame = StartWithGame,
    };

    /// <summary>Writes the game. False: a problem remains or the disk said no; the detail shows why.</summary>
    public async Task<bool> SaveAsync()
    {
        Recalculate();
        if (ProblemCount > 0)
        {
            return false;
        }

        GameEntry game = ToGame();
        try
        {
            await _catalog.SaveAsync(game, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Error(ex, "Game {Game} could not be saved", game.Name);
            ErrorMessage = UserMessages.Describe(ex);
            return false;
        }

        _initial = game;
        RenamedFrom = !IsNew && !string.Equals(Saved.Name, game.Name, StringComparison.Ordinal) ? Saved.Name : null;
        Saved = game;
        ErrorMessage = null;
        IsNew = false;
        Recalculate();
        _log.Information("Game {Game} saved from the detail", game.Name);
        return true;
    }

    public void Dispose()
    {
        Loc.Instance.PropertyChanged -= OnLanguageChanged;
        AppList.Changed -= OnPartChanged;
        AppList.Dispose();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!EditorFields<GameEditorViewModel>.Contains(e.PropertyName))
        {
            return;
        }

        if (e.PropertyName == nameof(ProcessName))
        {
            ProcessNotRecognised = false;
        }

        Recalculate();
    }

    private void OnPartChanged(object? sender, EventArgs e) => Recalculate();

    /// <summary>Validation and the dirty flag after every change; cheap enough to run on each keystroke.</summary>
    private void Recalculate()
    {
        if (_loading)
        {
            return;
        }

        GameEntry built = ToGame();
        IReadOnlyList<GameProblem> problems = GameEditing.Validate(built, _games, _profiles);
        ProblemCount = problems.Count;
        NameProblem = ProblemTexts.Of(problems, GameProblem.NameMissing, GameProblem.NameTooLong, GameProblem.NameTaken);
        LaunchProblem = ProblemTexts.Of(problems, GameProblem.LaunchMissing);
        HotkeyProblem = ProblemTexts.Of(problems, GameProblem.HotkeyInvalid, GameProblem.HotkeyTaken);
        AppsProblem = ProblemTexts.Of(problems, GameProblem.AppPathMissing);
        AppList.Problem = AppsProblem;
        IsDirty = IsNew || !StoredForm.Same(built, _initial);
    }

    private GameExitAction ExitFromChoice() => SelectedExit?.Key switch
    {
        nameof(GameExitKind.PreviousProfile) => new GameExitAction { Kind = GameExitKind.PreviousProfile, StopApps = StopApps },
        { } key when Guid.TryParse(key, out Guid id) => new GameExitAction { Kind = GameExitKind.Profile, ProfileId = id, StopApps = StopApps },
        _ => new GameExitAction { Kind = GameExitKind.Stay, StopApps = StopApps },
    };

    private static string ExitKeyOf(GameExitAction exit) => exit.Kind switch
    {
        GameExitKind.PreviousProfile => nameof(GameExitKind.PreviousProfile),
        GameExitKind.Profile => exit.ProfileId?.ToString() ?? nameof(GameExitKind.Stay),
        _ => nameof(GameExitKind.Stay),
    };

    /// <summary>Rebuilds the texts made in code and keeps every selection by key.</summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        string? profile = SelectedProfile?.Key;
        string? end = SelectedEnd?.Key;
        string? exit = SelectedExit?.Key;
        string? icon = SelectedIcon?.Key;

        _loading = true;
        try
        {
            FillChoices();
            SelectedProfile = ProfileOptions.FirstOrDefault(c => c.Key == profile) ?? NoProfileOption;
            SelectedEnd = EndChoices.FirstOrDefault(c => c.Key == end) ?? EndChoices[0];
            SelectedExit = ExitChoices.FirstOrDefault(c => c.Key == exit) ?? ExitChoices[0];
            SelectedIcon = IconChoices.FirstOrDefault(c => c.Key == icon) ?? IconChoices[0];
            AppList.Relabel();
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(HotkeyHint));
        ErrorMessage = null;
        OnPropertyChanged(nameof(WindowsText));
        OnPropertyChanged(nameof(ProcessHint));
        Recalculate();
    }

    private void FillChoices()
    {
        ProfileOptions.Clear();
        foreach (Profile profile in _profiles)
        {
            ProfileOptions.Add(new ProfileOption(
                profile.Id.ToString(), profile.Name, TopologyDisplays.From(profile.Displays), DescribeProfile(profile)));
        }

        ProfileOptions.Add(new ProfileOption(null, Loc.Instance["Game_NoProfile"], [], Loc.Instance["Game_NoProfileSub"]));

        EndChoices.Clear();
        EndChoices.Add(new Choice(nameof(SessionEnd.GameProcess), Loc.Instance["Game_EndsWithGame"]));
        EndChoices.Add(new Choice(nameof(SessionEnd.LauncherProcess), Loc.Instance["Game_EndsWithLauncher"]));

        ExitChoices.Clear();
        ExitChoices.Add(new Choice(nameof(GameExitKind.Stay), Loc.Instance["Game_ExitStay"]));
        ExitChoices.Add(new Choice(nameof(GameExitKind.PreviousProfile), Loc.Instance["Game_ExitPrevious"]));
        foreach (Profile profile in _profiles)
        {
            ExitChoices.Add(new Choice(profile.Id.ToString(), Loc.Format("Game_ExitProfile", profile.Name)));
        }

        IconChoices.Clear();
        IconChoices.Add(new Choice(null, Loc.Instance["Editor_IconNone"]));
        foreach (string key in ProfileIcons.All)
        {
            IconChoices.Add(new Choice(key, Loc.Instance["Icon_" + char.ToUpperInvariant(key[0]) + key[1..]]));
        }
    }

    /// <summary>"XG32UCWG, Left, Right · Ctrl+Alt+F1": the displays left to right, then the hotkey.</summary>
    private static string DescribeProfile(Profile profile)
    {
        string text = string.Join(", ", profile.Displays.OrderBy(d => d.PositionX).ThenBy(d => d.PositionY).Select(SwitchMessages.NameOf));
        return profile.Hotkey is { } hotkey ? text + " · " + HotkeyFormat.Format(hotkey) : text;
    }
}

/// <summary>One row of the profile choice on the game's "Profile" tab: picture XS, name, one line underneath.</summary>
/// <param name="Key">The profile id, or <c>null</c> for "don't switch anything".</param>
public sealed record ProfileOption(string? Key, string Name, IReadOnlyList<TopologyDisplay> Topology, string Sub)
{
    public bool HasTopology => Topology.Count > 0;

    public override string ToString() => Name;
}

/// <summary>One captured window on the games page: its title, the program and the size it gets back.</summary>
public sealed class SavedWindowRow(WindowPlacement placement)
{
    public string Name { get; } = string.IsNullOrWhiteSpace(placement.Title) ? placement.ProcessName : placement.Title;

    public string Details { get; } = placement.State switch
    {
        WindowState.Maximized => $"{placement.ProcessName} · {Localization.Loc.Instance["Game_WindowMaximized"]}",
        _ => string.Create(Localization.Loc.Instance.Culture, $"{placement.ProcessName} · {placement.Bounds.Width} × {placement.Bounds.Height}"),
    };

    /// <summary>"3840, 0" – where on the desktop it goes.</summary>
    public string Position { get; } = string.Create(Localization.Loc.Instance.Culture, $"{placement.Bounds.Left}, {placement.Bounds.Top}");
}
