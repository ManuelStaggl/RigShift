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
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// The start page: what the PC shows right now (live topology), the profiles to switch to, the games to start, the
/// attached displays with their names, and the recent switches.
/// </summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly IDisplayConfigurator _display;
    private readonly ProfileCatalog _catalog;
    private readonly SwitchCoordinator _coordinator;
    private readonly ProfileDialogs _dialogs;
    private readonly ProfilesViewModel _profiles;
    private readonly IAppShell _shell;
    private readonly IDisplaySizeReader _sizes;
    private readonly GameSessionService _sessions;
    private IReadOnlyList<AttachedDisplay> _attached = [];
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public OverviewViewModel(
        IDisplayConfigurator display,
        ProfileCatalog catalog,
        SwitchCoordinator coordinator,
        ProfileDialogs dialogs,
        ProfilesViewModel profiles,
        GameCatalog games,
        GameSessionService sessions,
        DisplayChangeWatcher watcher,
        IAppShell shell,
        IDisplaySizeReader sizes,
        TimeProvider time,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(log);
        _display = display;
        _catalog = catalog;
        _coordinator = coordinator;
        _dialogs = dialogs;
        _profiles = profiles;
        _shell = shell;
        _sizes = sizes;
        _sessions = sessions;
        Catalog = catalog;
        Games = games;
        Coordinator = coordinator;
        _time = time;
        _log = log.ForContext<OverviewViewModel>();

        catalog.Changed += (_, _) => OnUi(() => { UpdateActive(); UpdateProfilesTexts(); });
        catalog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProfileCatalog.ActiveProfile) or nameof(ProfileCatalog.IsEmpty))
            {
                OnUi(UpdateActive);
            }
        };
        coordinator.History.CollectionChanged += (_, _) => OnUi(RebuildHistory);
        coordinator.AppsCompleted += (_, _) => OnUi(RebuildHistory);
        watcher.DisplaysChanged += (_, _) => OnUi(() => RefreshCommand.Execute(null));
        Loc.Instance.PropertyChanged += (_, _) => OnUi(() =>
        {
            RebuildHistory();
            UpdateActive();
            RefreshCommand.Execute(null);
        });
        UpdateActive();
        RebuildHistory();
    }

    public ProfileCatalog Catalog { get; }

    public GameCatalog Games { get; }

    public SwitchCoordinator Coordinator { get; }

    public ObservableCollection<DisplayCard> Displays { get; } = [];

    public ObservableCollection<HistoryRow> History { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TopologyDisplay> LiveTopology { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>No active display at all: the topology surface shows a sentence instead.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasHistory { get; set; }

    /// <summary>Without profiles the side card offers the setup assistant instead of the active profile.</summary>
    [ObservableProperty]
    public partial bool HasProfiles { get; set; }

    /// <summary>"Showing now · Desk" over the live picture.</summary>
    [ObservableProperty]
    public partial string ActiveName { get; set; } = string.Empty;

    internal async Task RenameAsync(DisplayCard card, string? name)
    {
        try
        {
            await _catalog.RenameDisplayAsync(card.TargetDevicePath, name, CancellationToken.None);
            ErrorMessage = null;
            UpdateTopology();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Display {Display} could not be renamed", card.ModelName);
            ErrorMessage = Loc.Format("Status_Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            DisplaySnapshot snapshot = await Task.Run(() => _display.QueryAsync(CancellationToken.None));
            IReadOnlyDictionary<string, string> names = _catalog.KnownDisplayNames;
            Displays.Clear();
            _attached = snapshot.Displays;
            IReadOnlyList<(AttachedDisplay Display, int? Number)> numbered = DisplayNumbers.Assign(snapshot.Displays);
            foreach ((AttachedDisplay attached, int? shown) in numbered)
            {
                Displays.Add(new DisplayCard(this, attached, shown, names.GetValueOrDefault(attached.Identity.TargetDevicePath), ProfilesWith(attached)));
            }

            IsEmpty = Displays.Count == 0;
            ErrorMessage = null;
            UpdateTopology();
            _log.Information("Overview shows {Count} displays, {Active} active", Displays.Count, LiveTopology.Count);
        }
        catch (Win32Exception ex)
        {
            _log.Error(ex, "Displays could not be read");
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>"Switch" on a profile tile; on the active one it reads "Apply again" and does the same.</summary>
    [RelayCommand]
    private async Task SwitchAsync(ProfileItem? item)
    {
        if (item is not null)
        {
            _log.Information("Overview switches to {Profile}", item.Name);
            await _coordinator.SwitchAsync(item.Profile);
        }
    }

    [RelayCommand]
    private void Play(GameItem? item)
    {
        if (item is { CanPlay: true })
        {
            _log.Information("Overview starts {Game}", item.Name);
            _sessions.Start(item.Game);
        }
    }

    [RelayCommand]
    private void Identify()
    {
        var shown = Displays
            .Where(d => d.Number is not null && d.Mode is not null)
            .Select(d => (d.Number!.Value, d.Name, d.Mode!))
            .ToList();
        _log.Information("Identifying {Count} displays", shown.Count);
        Views.IdentifyWindow.ShowAll(shown);
    }

    [RelayCommand]
    private Task SetupAssistantAsync() => _dialogs.ShowSetupAssistantAsync();

    /// <summary>"Set up by hand": the profiles page with its "+ New" menu.</summary>
    [RelayCommand]
    private void SetupManually() => _shell.ShowMainWindow(typeof(Views.Pages.ProfilesPage));

    /// <summary>"Save the current arrangement as a profile": the profiles page with a new, unsaved profile (F3).</summary>
    [RelayCommand]
    private Task SaveCurrentAsync()
    {
        _shell.ShowMainWindow(typeof(Views.Pages.ProfilesPage));
        return _profiles.NewFromCurrentCommand.ExecuteAsync(null);
    }

    /// <summary>The active displays as the picture draws them, with the user's names.</summary>
    private void UpdateTopology()
    {
        LiveTopology = Displays
            .Where(d => d.Mode is not null)
            .Select(d => new TopologyDisplay
            {
                Key = d.TargetDevicePath,
                X = d.Mode!.PositionX,
                Y = d.Mode.PositionY,
                Width = d.Mode.Width,
                Height = d.Mode.Height,
                Number = d.Number,
                Name = d.Name,
                Mode = d.ModeText,
                Details = d.ModelName + " · " + d.ModeText,
                IsPrimary = d.Mode.IsPrimary,
            })
            .ToList();
    }

    private void UpdateActive()
    {
        HasProfiles = !_catalog.IsEmpty;
        ActiveName = _catalog.ActiveProfile?.Name ?? Loc.Instance["Tray_ActiveNone"];
    }

    private void RebuildHistory()
    {
        History.Clear();
        DateTimeOffset now = _time.GetUtcNow();
        foreach (SwitchRecord record in _coordinator.History.Where(r => r.Outcome != SwitchOutcome.DryRun))
        {
            History.Add(new HistoryRow(record, now));
        }

        HasHistory = History.Count > 0;
        UpdateActive();
    }

    private void UpdateProfilesTexts()
    {
        if (Displays.Count > 0)
        {
            RefreshCommand.Execute(null);
        }
    }

    private string ProfilesWith(AttachedDisplay attached)
    {
        List<string> names = _catalog.Profiles
            .Where(p => p.Displays.Any(d => string.Equals(d.Identity.TargetDevicePath, attached.Identity.TargetDevicePath, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();
        return names.Count == 0 ? Loc.Instance["Displays_NotInProfiles"] : string.Join(", ", names);
    }

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}

/// <summary>One line of the switch history: when, which profile, how it went, how long it took.</summary>
public sealed class HistoryRow(SwitchRecord record, DateTimeOffset now)
{
    public string TimeText { get; } = RelativeTime.Format(record.At, now, Loc.Instance.Culture);

    public string ProfileName { get; } = record.ProfileName;

    public StatusKind Kind { get; } = KindOf(record);

    /// <summary>The result wording of the toast (R-FLOW-2), with the missing displays or the error after it.</summary>
    public string ResultText { get; } = record.HasDetails ? record.OutcomeText + " · " + record.Details : record.OutcomeText;

    public string DurationText { get; } = record.Outcome is SwitchOutcome.Applied or SwitchOutcome.AppliedPartially ? record.DurationText : "—";

    public static StatusKind KindOf(SwitchRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Outcome switch
        {
            SwitchOutcome.Applied when record.Apps is AppsOutcome.Incomplete or AppsOutcome.DeviceMissing => StatusKind.Warn,
            SwitchOutcome.Applied when record.Audio == AudioOutcome.Incomplete => StatusKind.Warn,
            SwitchOutcome.Applied => StatusKind.Ok,
            SwitchOutcome.AppliedPartially or SwitchOutcome.RolledBack => StatusKind.Warn,
            SwitchOutcome.DryRun => StatusKind.Neutral,
            _ => StatusKind.Error,
        };
    }
}
