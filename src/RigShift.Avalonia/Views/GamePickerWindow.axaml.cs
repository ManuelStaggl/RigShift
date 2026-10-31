using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Avalonia.Services;
using RigShift.Core.Abstractions;
using RigShift.Windows.Ui;
using Serilog;

namespace RigShift.Avalonia.Views;

/// <summary>
/// Picks the game an entry starts: the installed Steam, Epic and other games read off the disk, or any program. Opened
/// from the editor for one game, and from the games page with tick boxes to add several at once. Keyboard: type to
/// search, Down into the list, Space ticks, Enter takes.
/// </summary>
public sealed partial class GamePickerWindow : Window
{
    private PickedGame? _chosen;
    private bool _done;

    public GamePickerWindow()
    {
        InitializeComponent();
        TextScale.Apply(this);
        DialogChrome.Apply(this);
        GameList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => SearchBox.Focus();
    }

    private GamePickerViewModel ViewModel => (GamePickerViewModel)DataContext!;

    /// <returns>The chosen game or program, or <c>null</c> if cancelled.</returns>
    internal static async Task<PickedGame?> PickAsync(GameEditorFactory factory)
    {
        GamePickerWindow window = await ShowAsync(factory, multiple: false);
        return window._chosen;
    }

    /// <returns>The games the user ticked, empty if cancelled.</returns>
    internal static async Task<IReadOnlyList<InstalledGame>> PickManyAsync(GameEditorFactory factory)
    {
        GamePickerWindow window = await ShowAsync(factory, multiple: true);
        return window._done ? window.ViewModel.Checked() : [];
    }

    private static async Task<GamePickerWindow> ShowAsync(GameEditorFactory factory, bool multiple)
    {
        var viewModel = new GamePickerViewModel(multiple);
        var window = new GamePickerWindow { DataContext = viewModel };
        if (multiple)
        {
            ToolTip.SetTip(window.IntroText, Loc.Instance["GamePicker_IntroManyTip"]);
        }

        Task fill = FillAsync(factory, viewModel);
        await AskDialog.ShowModalAsync(window);
        await fill;
        return window;
    }

    private static async Task FillAsync(GameEditorFactory factory, GamePickerViewModel viewModel)
    {
        IReadOnlyList<InstalledGame> found;
        try
        {
            found = await factory.FindInstalledAsync();
        }
        catch (Exception ex)
        {
            // Logged and shown as an empty list: browsing for the program still works.
            Log.Error(ex, "Installed games could not be listed");
            found = [];
        }

        viewModel.Fill(found);
        Log.Information("Game picker lists {Count} installed games", found.Count);
    }

    private void Choose()
    {
        if (ViewModel.IsMultiple)
        {
            // Enter on a row with nothing ticked yet takes that row.
            if (!ViewModel.CanChoose && ViewModel.Selected is { } selected)
            {
                selected.IsChecked = true;
            }

            if (ViewModel.CanChoose)
            {
                _done = true;
                Close();
            }

            return;
        }

        if (ViewModel.Selected is { } game)
        {
            Log.Information("Game picker chose {Game}", game.Name);
            _chosen = new PickedGame(game.Game, null);
            Close();
        }
    }

    private void OnChoose(object? sender, RoutedEventArgs e) => Choose();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || ViewModel.Games.Count == 0)
        {
            return;
        }

        e.Handled = true;
        ViewModel.Selected ??= ViewModel.Games[0];
        GameList.ScrollIntoView(ViewModel.Selected);
        GameList.ContainerFromItem(ViewModel.Selected)?.Focus(NavigationMethod.Directional);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && ViewModel.IsMultiple && ViewModel.Selected is { } game)
        {
            game.IsChecked = !game.IsChecked;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Choose();
            e.Handled = true;
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel.Selected is not { } game)
        {
            return;
        }

        if (ViewModel.IsMultiple)
        {
            game.IsChecked = !game.IsChecked;
            return;
        }

        Choose();
    }

    private void OnBrowse(object? sender, RoutedEventArgs e)
    {
        nint owner = TryGetPlatformHandle()?.Handle ?? 0;
        if (NativeWindow.PickFile(owner, Loc.Instance["App_FileFilter"], Loc.WithoutAccessKey(Loc.Instance["GamePicker_BrowseFile"]), null) is { } file)
        {
            Log.Information("Game picker chose file {File}", Path.GetFileName(file));
            _chosen = new PickedGame(null, file);
            Close();
        }
    }
}
