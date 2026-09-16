using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>What the picker returned: an installed game, or a program the user browsed for.</summary>
public sealed record PickedGame(InstalledGame? Installed, string? ExecutablePath);

/// <summary>
/// Picks the game an entry starts: the installed Steam and Epic games, read off the disk, or any program for
/// everything the sources do not know.
/// </summary>
public partial class GamePickerWindow : FluentWindow
{
    private readonly GamePickerViewModel _viewModel;

    private GamePickerWindow(GamePickerViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => GameList.Focus();
    }

    public PickedGame? Chosen { get; private set; }

    /// <returns>The chosen game, or <c>null</c> if cancelled.</returns>
    public static async Task<PickedGame?> PickAsync(Window? owner, GameDialogs dialogs)
    {
        ArgumentNullException.ThrowIfNull(dialogs);

        var viewModel = new GamePickerViewModel();
        var window = new GamePickerWindow(viewModel);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // Started before the dialog blocks: the list fills while the window is already up.
        Task<IReadOnlyList<InstalledGame>> finding = dialogs.FindInstalledAsync();
        _ = finding.ContinueWith(
            t => window.Dispatcher.Invoke(() => viewModel.Fill(t.Result)),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);

        return window.ShowDialog() == true ? window.Chosen : null;
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected is { } game)
        {
            Chosen = new PickedGame(game, null);
            DialogResult = true;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(GameList, source) is ListBoxItem { DataContext: InstalledGame game })
        {
            Chosen = new PickedGame(game, null);
            DialogResult = true;
        }
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

/// <summary>The installed games, while they are still being looked for.</summary>
public sealed partial class GamePickerViewModel : ObservableObject
{
    public GamePickerViewModel() => IsLoading = true;

    public ObservableCollection<InstalledGame> Games { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChoose))]
    public partial InstalledGame? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; }

    public bool IsEmpty => !IsLoading && Games.Count == 0;

    public bool CanChoose => Selected is not null;

    public void Fill(IReadOnlyList<InstalledGame> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        Games.Clear();
        foreach (InstalledGame game in found)
        {
            Games.Add(game);
        }

        IsLoading = false;
        OnPropertyChanged(nameof(IsEmpty));
    }
}
