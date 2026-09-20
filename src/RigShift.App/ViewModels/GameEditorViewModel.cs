using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// The game detail's editor: the tabs in the order a session runs them (game, profile, tools, windows, end), live
/// validation with a problem count, and the dirty flag for the save bar. Nothing is written until <see cref="SaveAsync"/>.
/// </summary>
public sealed partial class GameEditorViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlyList<AppAction> NoApps = [];

    private readonly GameEntry _original;
    private readonly IReadOnlyList<Profile> _profiles;
    private readonly IReadOnlyList<GameEntry> _games;
    private readonly GameCatalog _catalog;
    private readonly HotkeyService _hotkeys;
    private readonly ILogger _log;
    private GameEntry _initial;
    private GameLaunch _launch;
    private string _hotkeyHintKey = "Editor_HotkeyHint";
    private HotkeyUse? _hotkeyConflict;
    private bool _loading = true;

    /// <param name="games">Every configured game, this one included; names and hotkeys are checked against the others.</param>
    /// <param name="usbChoices">"Start right away" first, then the devices the tools can wait for.</param>
    /// <param name="usbWindowsNames">Windows' own name per device id, saved with the id.</param>
    public GameEditorViewModel(
        GameEntry game,
        bool isNew,
        IReadOnlyList<Profile> profiles,
        IReadOnlyList<GameEntry> games,
        IReadOnlyList<Choice> usbChoices,
        IReadOnlyDictionary<string, string> usbWindowsNames,
        GameCatalog catalog,
        HotkeyService hotkeys,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(usbChoices);
        ArgumentNullException.ThrowIfNull(usbWindowsNames);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(log);

        _original = game;
        _initial = game;
        _profiles = profiles;
        _games = games;
        _catalog = catalog;
        _hotkeys = hotkeys;
        _log = log.ForContext<GameEditorViewModel>();
        _launch = game.Launch;
        IsNew = isNew;
        UsbWindowsNames = usbWindowsNames;

        Name = game.Name;
        LaunchTarget = game.Launch.Target;
        ProcessName = game.Launch.ProcessName ?? string.Empty;
        LauncherProcessName = game.LauncherProcessName ?? string.Empty;
        StartWithGame = game.StartWithGame;
        StopApps = game.Exit.StopApps;
        WindowLayout = game.WindowLayout;
        Hotkey = game.Hotkey;
        HotkeyHint = HotkeyHintText();

        foreach (Choice choice in usbChoices)
        {
            UsbDevices.Add(choice);
        }

        string? waitFor = Core.Automation.UsbDeviceIds.Normalize(game.AppsWaitForUsbDeviceId);
        SelectedUsbDevice = UsbDevices.FirstOrDefault(c => c.Key == waitFor) ?? UsbDevices.FirstOrDefault();

        FillChoices();
        SelectedProfile = ProfileOptions.FirstOrDefault(c => c.Key == game.ProfileId?.ToString()) ?? NoProfileOption;
        SelectedEnd = EndChoices.First(c => c.Key == game.EndsWith.ToString());
        SelectedExit = ExitChoices.FirstOrDefault(c => c.Key == ExitKeyOf(game.Exit)) ?? ExitChoices[0];
        SelectedIcon = IconChoices.FirstOrDefault(c => c.Key == ProfileIcons.Normalize(game.Icon)) ?? IconChoices[0];

        foreach (AppAction app in game.Apps)
        {
            var item = new AppEditItem(app, showWhen: true);
            item.PropertyChanged += OnPartChanged;
            Apps.Add(item);
        }

        Apps.CollectionChanged += OnAppsChanged;
        Loc.Instance.PropertyChanged += OnLanguageChanged;
        _loading = false;
        Recalculate();
    }

    public Guid Id => _original.Id;

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
    [NotifyPropertyChangedFor(nameof(HasLaunchProblem))]
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
    public partial string Name { get; set; }

    /// <summary>What the game is started with: a program path, or the store's id.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchText), nameof(HasLaunch))]
    public partial string LaunchTarget { get; set; }

    /// <summary>"Steam", "Epic" or "EXE": the chip in front of the launch text.</summary>
    public string LaunchKindText => _launch.Kind switch
    {
        GameLaunchKind.Steam => "Steam",
        GameLaunchKind.Epic => "Epic",
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

    [ObservableProperty]
    public partial string HotkeyHint { get; set; }

    [ObservableProperty]
    public partial Choice? SelectedUsbDevice { get; set; }

    public ObservableCollection<Choice> UsbDevices { get; } = [];

    /// <summary>Windows' own name per device id, saved with the id so a disconnected device still reads as a name.</summary>
    public IReadOnlyDictionary<string, string> UsbWindowsNames { get; }

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

    public ObservableCollection<AppEditItem> Apps { get; } = [];

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
    public void BeginHotkeyRecording() => _hotkeys.Suspend();

    public void EndHotkeyRecording() => _hotkeys.Resume();

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

    internal void AddApp(string path, string? name = null) =>
        Apps.Add(new AppEditItem(new AppAction { Path = path, Name = name }, showWhen: true));

    /// <summary>A key combination pressed in the hotkey field; without Ctrl, Alt or Win it only shows a hint.</summary>
    internal void RecordHotkey(HotkeyModifiers modifiers, int virtualKey)
    {
        var hotkey = new Hotkey { Modifiers = modifiers, VirtualKey = virtualKey };
        if (!hotkey.IsValid)
        {
            SetHotkeyHint("Editor_HotkeyNeedsModifier");
            return;
        }

        // Hotkeys are suspended while the field has the focus, so this sees only other applications.
        if (!_hotkeys.IsAvailable(hotkey))
        {
            SetHotkeyHint("Problem_HotkeyInUse");
            return;
        }

        // The own hotkeys are released right now, so Windows cannot tell that a profile, a game or "back" holds this one.
        if (_hotkeys.UsedBy(hotkey, HotkeyUseKind.Game, _original.Id) is { } use)
        {
            _hotkeyConflict = use;
            HotkeyHint = HotkeyHintText();
            return;
        }

        Hotkey = hotkey;
        SetHotkeyHint("Editor_HotkeyHint");
        _log.Information("Game editor recorded hotkey {Hotkey}", HotkeyText);
    }

    [RelayCommand]
    private void ClearHotkey()
    {
        Hotkey = null;
        SetHotkeyHint("Editor_HotkeyHint");
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
    private void RemoveApp(AppEditItem? item)
    {
        if (item is not null)
        {
            Apps.Remove(item);
        }
    }

    [RelayCommand]
    private void MoveAppUp(AppEditItem? item) => MoveApp(item, -1);

    [RelayCommand]
    private void MoveAppDown(AppEditItem? item) => MoveApp(item, 1);

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
        Apps = [.. Apps.Select(a => a.ToAction())],
        AppsWaitForUsbDeviceId = SelectedUsbDevice?.Key,
        AppsWaitForUsbDeviceName = SelectedUsbDevice?.Key is null ? null : UsbWindowsNames.GetValueOrDefault(SelectedUsbDevice.Key),
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
            ErrorMessage = Loc.Format("Status_Error", ex.Message);
            return false;
        }

        _initial = game;
        ErrorMessage = null;
        IsNew = false;
        Recalculate();
        _log.Information("Game {Game} saved from the detail", game.Name);
        return true;
    }

    public void Dispose()
    {
        Loc.Instance.PropertyChanged -= OnLanguageChanged;
        Apps.CollectionChanged -= OnAppsChanged;
        foreach (AppEditItem app in Apps)
        {
            app.PropertyChanged -= OnPartChanged;
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Name) or nameof(LaunchTarget) or nameof(ProcessName) or nameof(LauncherProcessName)
            or nameof(StartWithGame) or nameof(StopApps) or nameof(WindowLayout) or nameof(Hotkey) or nameof(SelectedUsbDevice)
            or nameof(SelectedProfile) or nameof(SelectedEnd) or nameof(SelectedExit) or nameof(SelectedIcon))
        {
            if (e.PropertyName == nameof(ProcessName))
            {
                ProcessNotRecognised = false;
            }

            if (e.PropertyName == nameof(Name))
            {
                OnPropertyChanged(nameof(CommandText));
            }

            Recalculate();
        }
    }

    private void MoveApp(AppEditItem? item, int offset)
    {
        int index = item is null ? -1 : Apps.IndexOf(item);
        int target = index + offset;
        if (index >= 0 && target >= 0 && target < Apps.Count)
        {
            Apps.Move(index, target);
        }
    }

    private void OnAppsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (AppEditItem item in e.OldItems?.OfType<AppEditItem>() ?? [])
        {
            item.PropertyChanged -= OnPartChanged;
        }

        foreach (AppEditItem item in e.NewItems?.OfType<AppEditItem>() ?? [])
        {
            item.PropertyChanged += OnPartChanged;
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
        var problems = new List<string>();

        string name = built.Name;
        NameProblem = name.Length == 0
            ? Loc.Instance["Problem_NameMissing"]
            : name.Length > GameEntry.MaxNameLength
                ? Loc.Instance["Problem_NameTooLong"]
                : _games.Any(g => g.Id != Id && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
                    ? Loc.Instance["Problem_NameTaken"]
                    : null;
        Add(problems, NameProblem);

        LaunchProblem = built.Launch.Target.Length == 0 ? Loc.Instance["Problem_LaunchMissing"] : null;
        Add(problems, LaunchProblem);

        HotkeyProblem = built.Hotkey is { } hotkey
            && (_games.Any(g => g.Id != Id && g.Hotkey == hotkey) || _profiles.Any(p => p.Hotkey == hotkey))
            ? Loc.Instance["Problem_HotkeyTaken"]
            : null;
        Add(problems, HotkeyProblem);

        AppsProblem = built.Apps.Any(a => a.Path.Length == 0) ? Loc.Instance["Problem_AppPathMissing"] : null;
        Add(problems, AppsProblem);

        ProblemCount = problems.Count;
        IsDirty = IsNew || !SameGame(built, _initial);
    }

    private static void Add(List<string> problems, string? problem)
    {
        if (problem is not null)
        {
            problems.Add(problem);
        }
    }

    private static bool SameGame(GameEntry a, GameEntry b) =>
        a with { Apps = NoApps } == b with { Apps = NoApps } && a.Apps.SequenceEqual(b.Apps);

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

    /// <summary>The hint in the current language; a combination taken inside RigShift names who holds it.</summary>
    private string HotkeyHintText() => _hotkeyConflict is { } use ? HotkeyService.UsedByText(use) : Loc.Instance[_hotkeyHintKey];

    private void SetHotkeyHint(string key)
    {
        _hotkeyConflict = null;
        _hotkeyHintKey = key;
        HotkeyHint = Loc.Instance[key];
    }

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
            foreach (AppEditItem app in Apps)
            {
                app.Relabel();
            }
        }
        finally
        {
            _loading = false;
        }

        HotkeyHint = HotkeyHintText();
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
