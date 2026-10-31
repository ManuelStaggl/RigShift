using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using Serilog;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;


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
        TextScale.Apply(this);
        Loaded += (_, _) => SearchBox.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        BrandWindow.ApplyChrome(this);
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
            t =>
            {
                if (t.Exception is { } failure)
                {
                    Serilog.Log.Error(failure, "Installed games could not be listed");
                }

                window.Dispatcher.Invoke(() => viewModel.Fill(t.IsCompletedSuccessfully ? t.Result : []));
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
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
