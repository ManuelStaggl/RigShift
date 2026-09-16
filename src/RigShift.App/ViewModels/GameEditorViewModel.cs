using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

/// <summary>
/// The game editor. Sections in the order a session runs them: the game, the profile, the companion apps, the window
/// positions, the end.
/// </summary>
public sealed partial class GameEditorViewModel : ObservableObject
{
    private readonly GameEntry _original;
    private readonly IReadOnlyList<Profile> _profiles;
    private readonly IReadOnlyList<string> _otherNames;

    private GameLaunch _launch;

    public GameEditorViewModel(GameEntry game, IReadOnlyList<Profile> profiles, IReadOnlyList<string> otherNames, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(otherNames);

        _original = game;
        _profiles = profiles;
        _otherNames = otherNames;
        _launch = game.Launch;
        IsNew = isNew;

        Name = game.Name;
        LaunchTarget = game.Launch.Target;
        ProcessName = game.Launch.ProcessName ?? string.Empty;
        LauncherProcessName = game.LauncherProcessName ?? string.Empty;
        StartWithGame = game.StartWithGame;
        StopApps = game.Exit.StopApps;
        WindowLayout = game.WindowLayout;
        HotkeyText = HotkeyFormatText(game.Hotkey);
        Hotkey = game.Hotkey;

        FillChoices();
        SelectedProfile = ProfileChoices.FirstOrDefault(c => c.Key == game.ProfileId?.ToString()) ?? ProfileChoices[0];
        SelectedEnd = EndChoices.First(c => c.Key == game.EndsWith.ToString());
        SelectedExit = ExitChoices.FirstOrDefault(c => c.Key == ExitKeyOf(game.Exit)) ?? ExitChoices[0];
        SelectedIcon = IconChoices.FirstOrDefault(c => c.Key == ProfileIcons.Normalize(game.Icon)) ?? IconChoices[0];

        foreach (AppAction app in game.Apps)
        {
            Apps.Add(new AppEditItem(app, showWhen: true));
        }

        Loc.Instance.PropertyChanged += (_, _) => Relabel();
    }

    public bool IsNew { get; }

    public string Title => IsNew ? Loc.Instance["Games_Add"] : Loc.Instance["Games_Edit"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(NameError), nameof(HasNameError))]
    public partial string Name { get; set; }

    /// <summary>What the game is started with: a program path, or the store's id.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(LaunchDescription))]
    public partial string LaunchTarget { get; set; }

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
    [NotifyPropertyChangedFor(nameof(WindowsText), nameof(HasWindows))]
    public partial WindowLayout? WindowLayout { get; set; }

    [ObservableProperty]
    public partial Hotkey? Hotkey { get; set; }

    [ObservableProperty]
    public partial string HotkeyText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsbWaitSet))]
    public partial Choice? SelectedUsbDevice { get; set; }

    public ObservableCollection<Choice> UsbDevices { get; } = [];

    /// <summary>Windows' own name per device id, saved with the id so a disconnected device still reads as a name.</summary>
    public Dictionary<string, string> UsbWindowsNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsUsbWaitSet => SelectedUsbDevice?.Key is not null;

    public ObservableCollection<Choice> ProfileChoices { get; } = [];

    [ObservableProperty]
    public partial Choice? SelectedProfile { get; set; }

    public ObservableCollection<Choice> EndChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsLauncherProcess))]
    public partial Choice? SelectedEnd { get; set; }

    public bool NeedsLauncherProcess => SelectedEnd?.Key == nameof(SessionEnd.LauncherProcess);

    public ObservableCollection<Choice> ExitChoices { get; } = [];

    [ObservableProperty]
    public partial Choice? SelectedExit { get; set; }

    public ObservableCollection<Choice> IconChoices { get; } = [];

    [ObservableProperty]
    public partial Choice? SelectedIcon { get; set; }

    public ObservableCollection<AppEditItem> Apps { get; } = [];

    /// <summary>"Steam · 266410" or the program path – what the entry starts, in words.</summary>
    public string LaunchDescription => _launch.Kind switch
    {
        GameLaunchKind.Steam => "Steam · " + LaunchTarget,
        GameLaunchKind.Epic => "Epic · " + LaunchTarget,
        _ => LaunchTarget,
    };

    public string WindowsText => WindowLayout is { IsEmpty: false } layout
        ? Loc.Format(
            layout.Windows.Count == 1 ? "Game_WindowsSavedAtOne" : "Game_WindowsSavedAt",
            layout.Windows.Count,
            layout.CapturedAt.ToLocalTime().ToString("g", Loc.Instance.Culture))
        : Loc.Instance["Game_WindowsNone"];

    public bool HasWindows => WindowLayout is { IsEmpty: false };

    public string? NameError => string.IsNullOrWhiteSpace(Name)
        ? Loc.Instance["Editor_NameRequired"]
        : _otherNames.Contains(Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            ? Loc.Instance["Editor_NameTaken"]
            : null;

    /// <summary>Without it the empty error line would leave a gap under the name box.</summary>
    public bool HasNameError => NameError is not null;

    public bool CanSave => NameError is null && !string.IsNullOrWhiteSpace(LaunchTarget);

    /// <summary>Takes a game the user picked from the installed ones, with everything the template knows.</summary>
    public void SetLaunch(InstalledGame installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        _launch = installed.Launch;
        LaunchTarget = installed.Launch.Target;
        if (string.IsNullOrWhiteSpace(Name) || Name == Loc.Instance["Games_NewName"])
        {
            Name = installed.Name;
        }

        GameEntry filled = SimTemplates.Apply(new GameEntry { Id = _original.Id, Name = installed.Name, Launch = installed.Launch });
        ProcessName = filled.Launch.ProcessName ?? string.Empty;
        LauncherProcessName = filled.LauncherProcessName ?? string.Empty;
        SelectedEnd = EndChoices.First(c => c.Key == filled.EndsWith.ToString());
        OnPropertyChanged(nameof(LaunchDescription));
    }

    /// <summary>Takes a program the user browsed for.</summary>
    public void SetExecutable(string path)
    {
        _launch = new GameLaunch { Kind = GameLaunchKind.Executable, Target = path };
        LaunchTarget = path;
        ProcessName = string.Empty;
        OnPropertyChanged(nameof(LaunchDescription));
    }

    internal void AddApp(string path, string? name = null) =>
        Apps.Add(new AppEditItem(new AppAction { Path = path, Name = name }, showWhen: true));

    [RelayCommand]
    private void RemoveApp(AppEditItem? item)
    {
        if (item is not null)
        {
            Apps.Remove(item);
        }
    }

    [RelayCommand]
    private void MoveAppUp(AppEditItem? item)
    {
        int index = item is null ? -1 : Apps.IndexOf(item);
        if (index > 0)
        {
            Apps.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveAppDown(AppEditItem? item)
    {
        int index = item is null ? -1 : Apps.IndexOf(item);
        if (index >= 0 && index < Apps.Count - 1)
        {
            Apps.Move(index, index + 1);
        }
    }

    [RelayCommand]
    private void ClearWindows() => WindowLayout = null;

    [RelayCommand]
    private void ClearHotkey()
    {
        Hotkey = null;
        HotkeyText = string.Empty;
    }

    internal void RecordHotkey(HotkeyModifiers modifiers, int virtualKey)
    {
        Hotkey = new Hotkey { Modifiers = modifiers, VirtualKey = virtualKey };
        HotkeyText = HotkeyFormatText(Hotkey);
    }

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

    private static string HotkeyFormatText(Hotkey? hotkey) =>
        hotkey is null ? string.Empty : Services.HotkeyFormat.Format(hotkey);

    private void Relabel()
    {
        string? profile = SelectedProfile?.Key;
        string? end = SelectedEnd?.Key;
        string? exit = SelectedExit?.Key;
        string? icon = SelectedIcon?.Key;

        FillChoices();

        SelectedProfile = ProfileChoices.FirstOrDefault(c => c.Key == profile) ?? ProfileChoices[0];
        SelectedEnd = EndChoices.FirstOrDefault(c => c.Key == end) ?? EndChoices[0];
        SelectedExit = ExitChoices.FirstOrDefault(c => c.Key == exit) ?? ExitChoices[0];
        SelectedIcon = IconChoices.FirstOrDefault(c => c.Key == icon) ?? IconChoices[0];

        foreach (AppEditItem app in Apps)
        {
            app.Relabel();
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(WindowsText));
        OnPropertyChanged(nameof(NameError));
    }

    private void FillChoices()
    {
        ProfileChoices.Clear();
        ProfileChoices.Add(new Choice(null, Loc.Instance["Game_NoProfile"]));
        foreach (Profile profile in _profiles)
        {
            ProfileChoices.Add(new Choice(profile.Id.ToString(), profile.Name));
        }

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
}
