using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.App.Services;

namespace RigShift.App.ViewModels;

/// <summary>
/// The tray popup: the profiles with their pictures, the games below them and the three actions. It is what the tool
/// is for on a normal day, so it carries no header and no settings – whatever is not switching or starting belongs
/// in the window.
/// </summary>
public sealed partial class TrayPopupViewModel : ObservableObject
{
    private readonly IAppShell _shell;
    private readonly GameSessionService _sessions;
    private readonly AutomationService? _automation;

    /// <summary>Resolved on the click, not in the constructor: the profiles page pulls in the whole window.</summary>
    private readonly Func<ProfilesViewModel> _profiles;

    public TrayPopupViewModel(
        ProfileCatalog catalog,
        GameCatalog games,
        GameSessionService sessions,
        SwitchCoordinator coordinator,
        Func<ProfilesViewModel> profiles,
        IAppShell shell,
        AutomationService? automation = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(coordinator);
        Catalog = catalog;
        Games = games;
        Coordinator = coordinator;
        _sessions = sessions;
        _profiles = profiles;
        _shell = shell;
        _automation = automation;
        if (automation is not null)
        {
            automation.Changed += (_, _) => OnAutomationChanged();
        }

        catalog.ProfilesChanged += (_, _) => OnStatusChanged();
        Loc.Instance.PropertyChanged += (_, _) => OnStatusChanged();

        // The games list is the catalog's own; nobody else marks its items as running, so the popup does it.
        games.Changed += (_, _) => UpdateRunning();
        sessions.SessionChanged += (_, _) => UpdateRunning();
        coordinator.PropertyChanged += OnCoordinatorChanged;
        UpdateRunning();
    }

    public event EventHandler? CloseRequested;

    public ProfileCatalog Catalog { get; }

    public GameCatalog Games { get; }

    public SwitchCoordinator Coordinator { get; }

    /// <summary>Shown in place of the rows when there is no profile yet.</summary>
    public string? StatusText => Catalog.IsEmpty ? Loc.Instance["Tray_NoProfiles"] : null;

    public bool HasStatusText => StatusText is not null;

    /// <summary>Up to four profiles show as tiles with a picture; more as compact rows.</summary>
    public bool UseTiles => Catalog.Items.Count is > 0 and <= 4;

    public bool UseRows => Catalog.Items.Count > 4;

    /// <summary>The pause switch in the header only shows when there is a rule to pause.</summary>
    public bool HasRules => _automation?.Rules.Count > 0;

    public bool IsPaused => _automation?.IsPaused == true;

    /// <summary>"Switching to Sim Rig …" above the progress line; the plain sentence when the target is unknown.</summary>
    public string SwitchingText => Coordinator.SwitchingProfile is { } profile
        ? Loc.Format("Tray_SwitchingTo", profile.Name)
        : Loc.Instance["Tray_Switching"];

    [RelayCommand]
    private async Task SwitchAsync(ProfileItem? item)
    {
        if (item is null)
        {
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
        await Coordinator.SwitchAsync(item.Profile);
    }

    [RelayCommand]
    private void Play(GameItem? item)
    {
        if (item is null || item.IsRunning)
        {
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
        _sessions.Start(item.Game);
    }

    /// <summary>Opens the profile page with the current arrangement as a new, unsaved profile – as the tray menu does.</summary>
    [RelayCommand]
    private void SaveCurrent()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _shell.ShowMainWindow(typeof(Views.Pages.ProfilesPage));
        _profiles().NewFromCurrentCommand.Execute(null);
    }

    [RelayCommand]
    private void Open()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _shell.ShowMainWindow();
    }

    [RelayCommand]
    private void Exit() => _shell.QuitByUser();

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        if (_automation is not null)
        {
            await _automation.SetPausedAsync(!_automation.IsPaused);
            OnAutomationChanged();
        }
    }

    /// <summary>Digits 1–9 in the popup switch to the profile at that place.</summary>
    public bool SwitchToNumber(int number)
    {
        if (!Coordinator.IsIdle || number < 1 || number > Catalog.Items.Count)
        {
            return false;
        }

        SwitchCommand.Execute(Catalog.Items[number - 1]);
        return true;
    }

    private void OnAutomationChanged()
    {
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(IsPaused));
    }

    private void OnCoordinatorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SwitchCoordinator.SwitchingProfile) or nameof(SwitchCoordinator.IsSwitching))
        {
            OnPropertyChanged(nameof(SwitchingText));
            Guid? target = Coordinator.IsSwitching ? Coordinator.SwitchingProfile?.Id : null;
            foreach (ProfileItem item in Catalog.Items)
            {
                item.IsSwitchTarget = target is { } id && item.Profile.Id == id;
            }
        }
    }

    private void UpdateRunning()
    {
        foreach (GameItem item in Games.Items)
        {
            item.IsRunning = _sessions.IsRunning(item.Game.Id);
        }
    }

    private void OnStatusChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatusText));
        OnPropertyChanged(nameof(UseTiles));
        OnPropertyChanged(nameof(UseRows));
        OnPropertyChanged(nameof(SwitchingText));
    }
}
