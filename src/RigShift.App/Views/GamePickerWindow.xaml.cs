using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using Serilog;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>What the picker returned: an installed game, or a program the user browsed for.</summary>
public sealed record PickedGame(InstalledGame? Installed, string? ExecutablePath);

/// <summary>
/// Picks the game an entry starts: the installed Steam and Epic games, read off the disk, or any program for
/// everything the sources do not know. Opened from the editor for one game, and from the games page to add several
/// at once – whoever has five sims installed should not walk through the editor five times.
/// </summary>
public partial class GamePickerWindow : FluentWindow
{
    private readonly GamePickerViewModel _viewModel;

    private GamePickerWindow(GamePickerViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => SearchBox.Focus();
    }

    public PickedGame? Chosen { get; private set; }

    /// <returns>The chosen game, or <c>null</c> if cancelled.</returns>
    public static Task<PickedGame?> PickAsync(Window? owner, GameDialogs dialogs)
    {
        GamePickerWindow window = Build(owner, dialogs, multiple: false);
        return Task.FromResult(window.ShowDialog() == true ? window.Chosen : null);
    }

    /// <returns>The games the user ticked, empty if cancelled.</returns>
    public static Task<IReadOnlyList<InstalledGame>> PickManyAsync(Window? owner, GameDialogs dialogs)
    {
        GamePickerWindow window = Build(owner, dialogs, multiple: true);
        return Task.FromResult(window.ShowDialog() == true ? window._viewModel.Checked() : []);
    }

    /// <summary>Builds the window and starts the search; the list fills while the window is already up.</summary>
    private static GamePickerWindow Build(Window? owner, GameDialogs dialogs, bool multiple)
    {
        ArgumentNullException.ThrowIfNull(dialogs);

        var viewModel = new GamePickerViewModel(multiple);
        var window = new GamePickerWindow(viewModel);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Task<IReadOnlyList<InstalledGame>> finding = dialogs.FindInstalledAsync();
        _ = finding.ContinueWith(
            t => window.Dispatcher.Invoke(() => viewModel.Fill(t.Result)),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);

        return window;
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsMultiple)
        {
            DialogResult = _viewModel.Checked().Count > 0;
            return;
        }

        if (_viewModel.Selected is { } game)
        {
            Chosen = new PickedGame(game.Game, null);
            DialogResult = true;
        }
    }

    /// <summary>Arrow down leaves the search for the list, on the selected (or first) game.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key != Key.Down || _viewModel.Games.Count == 0)
        {
            return;
        }

        e.Handled = true;
        _viewModel.Selected ??= _viewModel.Games[0];
        GameList.ScrollIntoView(_viewModel.Selected);
        if (GameList.ItemContainerGenerator.ContainerFromItem(_viewModel.Selected) is ListBoxItem item)
        {
            item.Focus();
        }
    }

    /// <summary>A double click takes one game; while several are being picked it only ticks the row.</summary>
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(GameList, source) is not ListBoxItem { DataContext: InstalledGameItem game })
        {
            return;
        }

        if (_viewModel.IsMultiple)
        {
            game.IsChecked = !game.IsChecked;
            return;
        }

        Chosen = new PickedGame(game.Game, null);
        DialogResult = true;
    }

    /// <summary>Everything the sources do not know: iRacing's own installer, a sim from a folder, a launcher.</summary>
    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.Instance["App_FileFilter"],
            Title = Loc.Instance["GamePicker_BrowseFile"],
        };

        if (dialog.ShowDialog(this) == true)
        {
            Chosen = new PickedGame(null, dialog.FileName);
            DialogResult = true;
        }
    }
}

/// <summary>One installed game in the list; its icon arrives after the list is up.</summary>
public sealed partial class InstalledGameItem(InstalledGame game) : ObservableObject
{
    public InstalledGame Game { get; } = game;

    public string Name => Game.Name;

    public string Source => Game.Source;

    public bool HasSource => Source.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    public partial ImageSource? Icon { get; set; }

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

    /// <summary>Icons after the list: a store game's executable may sit deep in its install folder.</summary>
    private static async Task LoadIconsAsync(IReadOnlyList<InstalledGameItem> items)
    {
        foreach (InstalledGameItem item in items)
        {
            item.Icon = await GameIcons.LoadAsync(item.Game.Launch, Log.Logger);
        }
    }

    partial void OnQueryChanged(string value) => Filter();

    /// <summary>With a search, the best match is selected, so Enter takes it.</summary>
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
