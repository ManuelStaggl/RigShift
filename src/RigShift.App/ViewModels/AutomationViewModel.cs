using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>Game rules: "when this game starts, switch to that profile" (docs/PLAN.md, section 6). Changes save at once.</summary>
public sealed partial class AutomationViewModel : ObservableObject
{
    internal const string CustomGameKey = "custom";
    private const string ExitStayKey = "stay";
    private const string ExitBackKey = "back";
    private const string ExitToPrefix = "to:";

    private readonly SettingsService _settings;
    private readonly ProfileCatalog _catalog;
    private readonly AutomationService _automation;
    private readonly ILogger _log;
    private bool _loading;

    public AutomationViewModel(SettingsService settings, ProfileCatalog catalog, AutomationService automation, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(log);

        _settings = settings;
        _catalog = catalog;
        _automation = automation;
        _log = log.ForContext<AutomationViewModel>();
        automation.Changed += (_, _) => Quietly(() => IsPaused = automation.IsPaused);
    }

    public ObservableCollection<RuleCard> Rules { get; } = [];

    public ObservableCollection<Choice> GameChoices { get; } = [];

    public ObservableCollection<Choice> ProfileChoices { get; } = [];

    public ObservableCollection<Choice> ExitChoices { get; } = [];

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddRuleCommand))]
    public partial bool HasNoProfiles { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    public void Load() => Quietly(() =>
    {
        IReadOnlyList<AutomationRule> rules = _automation.Rules;

        GameChoices.Clear();
        foreach (GameTemplate template in GameTemplates.BuiltIn)
        {
            GameChoices.Add(new Choice(template.Id, template.Name));
        }

        GameChoices.Add(new Choice(CustomGameKey, Loc.Instance["Automation_CustomGame"]));

        ProfileChoices.Clear();
        ExitChoices.Clear();
        ExitChoices.Add(new Choice(ExitStayKey, Loc.Instance["Automation_ExitStay"]));
        ExitChoices.Add(new Choice(ExitBackKey, Loc.Instance["Automation_ExitBack"]));
        foreach (Profile profile in _catalog.Profiles)
        {
            ProfileChoices.Add(new Choice(profile.Id.ToString("D"), profile.Name));
            ExitChoices.Add(new Choice(ExitToPrefix + profile.Id.ToString("D"), Loc.Format("Automation_ExitTo", profile.Name)));
        }

        // A rule may point at a deleted profile; keep it visible instead of silently changing the rule.
        foreach (Guid missing in rules.SelectMany(r => new[] { r.ProfileId, r.ExitProfileId ?? r.ProfileId }).Distinct().Where(id => _catalog.Find(id) is null))
        {
            ProfileChoices.Add(new Choice(missing.ToString("D"), Loc.Instance["Automation_MissingProfile"]));
            ExitChoices.Add(new Choice(ExitToPrefix + missing.ToString("D"), Loc.Format("Automation_ExitTo", Loc.Instance["Automation_MissingProfile"])));
        }

        Rules.Clear();
        foreach (AutomationRule rule in rules)
        {
            Rules.Add(new RuleCard(this, rule));
        }

        IsPaused = _automation.IsPaused;
        HasNoProfiles = _catalog.Profiles.Count == 0;
        IsEmpty = Rules.Count == 0;
    });

    internal Choice? GameChoiceFor(AutomationRule rule) =>
        GameChoices.FirstOrDefault(c => c.Key == (rule.TemplateId ?? CustomGameKey));

    internal Choice? ProfileChoiceFor(Guid id) => ProfileChoices.FirstOrDefault(c => c.Key == id.ToString("D"));

    internal Choice? ExitChoiceFor(AutomationRule rule) => rule.OnExit switch
    {
        ExitAction.SwitchBack => ExitChoices.FirstOrDefault(c => c.Key == ExitBackKey),
        ExitAction.SwitchTo when rule.ExitProfileId is { } id => ExitChoices.FirstOrDefault(c => c.Key == ExitToPrefix + id.ToString("D")),
        _ => ExitChoices.FirstOrDefault(c => c.Key == ExitStayKey),
    };

    internal static (ExitAction Action, Guid? Profile) ExitFrom(Choice? choice) => choice?.Key switch
    {
        ExitBackKey => (ExitAction.SwitchBack, null),
        { } key when key.StartsWith(ExitToPrefix, StringComparison.Ordinal) && Guid.TryParse(key[ExitToPrefix.Length..], out Guid id) => (ExitAction.SwitchTo, id),
        _ => (ExitAction.Stay, null),
    };

    internal void OnCardChanged()
    {
        if (!_loading)
        {
            _ = SaveAsync();
        }
    }

    partial void OnIsPausedChanged(bool value)
    {
        if (!_loading)
        {
            _ = _automation.SetPausedAsync(value);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddRule))]
    private async Task AddRuleAsync()
    {
        var rule = new AutomationRule
        {
            TemplateId = GameTemplates.BuiltIn.Count > 0 ? GameTemplates.BuiltIn[0].Id : null,
            ProfileId = _catalog.Profiles.FirstOrDefault(p => p.Id == _settings.Current.DefaultProfileId)?.Id ?? _catalog.Profiles[0].Id,
            OnExit = ExitAction.SwitchBack,
        };
        Quietly(() => Rules.Add(new RuleCard(this, rule)));
        IsEmpty = false;
        _log.Information("Automation rule {Rule} added", rule.Id);
        await SaveAsync();
    }

    private bool CanAddRule() => !HasNoProfiles;

    [RelayCommand]
    private async Task DeleteRuleAsync(RuleCard? card)
    {
        if (card is null)
        {
            return;
        }

        Rules.Remove(card);
        IsEmpty = Rules.Count == 0;
        _log.Information("Automation rule {Rule} deleted", card.Id);
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        List<AutomationRule> rules = Rules.Select(r => r.ToRule()).ToList();
        try
        {
            await _settings.UpdateAsync(s => s with { AutomationRules = rules }, CancellationToken.None);
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Automation rules could not be saved");
            ErrorMessage = Loc.Format("Status_Error", ex.Message);
        }
    }

    private void Quietly(Action action)
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            action();
        }
        finally
        {
            _loading = wasLoading;
        }
    }
}

/// <summary>One rule on the automation page.</summary>
public sealed partial class RuleCard : ObservableObject
{
    private readonly AutomationViewModel _owner;

    public RuleCard(AutomationViewModel owner, AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(rule);

        _owner = owner;
        Id = rule.Id;
        IsEnabled = rule.IsEnabled;
        SelectedGame = owner.GameChoiceFor(rule);
        ExecutablePath = rule.ExecutablePath ?? string.Empty;
        SelectedProfile = owner.ProfileChoiceFor(rule.ProfileId);
        SelectedExit = owner.ExitChoiceFor(rule);
        SkipConfirmation = rule.SkipConfirmation;
    }

    public Guid Id { get; }

    public AutomationViewModel Owner => _owner;

    public bool IsCustom => SelectedGame?.Key == AutomationViewModel.CustomGameKey;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom))]
    public partial Choice? SelectedGame { get; set; }

    [ObservableProperty]
    public partial string ExecutablePath { get; set; }

    [ObservableProperty]
    public partial Choice? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial Choice? SelectedExit { get; set; }

    [ObservableProperty]
    public partial bool SkipConfirmation { get; set; }

    public AutomationRule ToRule()
    {
        (ExitAction onExit, Guid? exitProfile) = AutomationViewModel.ExitFrom(SelectedExit);
        return new AutomationRule
        {
            Id = Id,
            IsEnabled = IsEnabled,
            TemplateId = IsCustom ? null : SelectedGame?.Key,
            ExecutablePath = IsCustom && !string.IsNullOrWhiteSpace(ExecutablePath) ? ExecutablePath.Trim() : null,
            ProfileId = Guid.TryParse(SelectedProfile?.Key, out Guid profile) ? profile : Guid.Empty,
            OnExit = onExit,
            ExitProfileId = exitProfile,
            SkipConfirmation = SkipConfirmation,
        };
    }

    partial void OnIsEnabledChanged(bool value) => _owner.OnCardChanged();

    partial void OnSelectedGameChanged(Choice? value) => _owner.OnCardChanged();

    partial void OnExecutablePathChanged(string value) => _owner.OnCardChanged();

    partial void OnSelectedProfileChanged(Choice? value) => _owner.OnCardChanged();

    partial void OnSelectedExitChanged(Choice? value) => _owner.OnCardChanged();

    partial void OnSkipConfirmationChanged(bool value) => _owner.OnCardChanged();
}
