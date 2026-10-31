using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>One installed game in the list; its icon arrives after the list is up.</summary>
public sealed partial class InstalledGameItem(InstalledGame game) : ObservableObject
{
    public InstalledGame Game { get; } = game;

    public string Name => Game.Name;

    public string Source => Game.Source;

    public bool HasSource => Source.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    public partial object? Icon { get; set; }

    public bool HasIcon => Icon is not null;

    /// <summary>Ticked for adding; only used while several games are being picked.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>The installed games, while they are still being looked for.</summary>
public sealed partial class GamePickerViewModel : ObservableObject
{
    private IReadOnlyList<InstalledGameItem> _all = [];

    public GamePickerViewModel(bool multiple = false)
    {
        IsMultiple = multiple;
        IsLoading = true;
        Query = string.Empty;
    }

    /// <summary>Several games at once: every row gets a tick box and the button adds all of them.</summary>
    public bool IsMultiple { get; }

    public bool IsSingle => !IsMultiple;

    /// <summary>The window says which of the two jobs it is doing – picking one game, or adding several.</summary>
    public string WindowTitle => Loc.Instance[IsMultiple ? "GamePicker_TitleMany" : "GamePicker_Title"];

    public string Intro => Loc.Instance[IsMultiple ? "GamePicker_IntroMany" : "GamePicker_Intro"];

    public ObservableCollection<InstalledGameItem> Games { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChoose))]
    public partial InstalledGameItem? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; }

    public bool IsEmpty => !IsLoading && Games.Count == 0;

    public bool CanChoose => IsMultiple ? _all.Any(g => g.IsChecked) : Selected is not null;

    /// <summary>"Add" counts what is ticked, so the button says what will happen.</summary>
    public string ChooseText => IsMultiple
        ? Loc.Format("GamePicker_AddCount", _all.Count(g => g.IsChecked))
        : Loc.Instance["AppPicker_Choose"];

    public IReadOnlyList<InstalledGame> Checked() => [.. _all.Where(g => g.IsChecked).Select(g => g.Game)];

    public void Fill(IReadOnlyList<InstalledGame> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        _all = [.. found.Select(g => new InstalledGameItem(g))];
        foreach (InstalledGameItem item in _all)
        {
            item.PropertyChanged += OnItemChanged;
        }

        IsLoading = false;
        Filter();
        _ = LoadIconsAsync(_all);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledGameItem.IsChecked))
        {
            OnPropertyChanged(nameof(CanChoose));
            OnPropertyChanged(nameof(ChooseText));
        }
    }

    // Icons after the list: a store game's executable may sit deep in its install folder.
    private static async Task LoadIconsAsync(IReadOnlyList<InstalledGameItem> items)
    {
        foreach (InstalledGameItem item in items)
        {
            item.Icon = await GameIcons.LoadAsync(item.Game.Launch, Log.Logger);
        }
    }

    partial void OnQueryChanged(string value) => Filter();

    // The best match is selected, so Enter takes it.
    private void Filter()
    {
        InstalledGameItem? selected = Selected;
        Games.Clear();
        foreach (InstalledGameItem game in _all.Where(
            g => Query.Length == 0 || g.Name.Contains(Query.Trim(), StringComparison.CurrentCultureIgnoreCase)))
        {
            Games.Add(game);
        }

        Selected = selected is not null && Games.Contains(selected) ? selected
            : Query.Length > 0 && Games.Count > 0 ? Games[0]
            : null;
        OnPropertyChanged(nameof(IsEmpty));
    }
}
