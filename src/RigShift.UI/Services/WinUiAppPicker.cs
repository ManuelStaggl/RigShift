using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Windows.Apps;
using RigShift.Windows.Ui;
using Serilog;

namespace RigShift.UI.Services;

/// <summary>
/// Picks a program for a profile or game: installed (Start menu) and running apps with a search, or any file through the
/// Windows "Open" dialog for tools without a Start menu entry.
/// </summary>
internal sealed class WinUiAppPicker : IAppPicker
{
    public async Task<PickedApp?> PickAsync(string? currentPath)
    {
        var viewModel = new AppPickerViewModel(() => AppDiscovery.Find(Log.Logger), AppIcons.Load, Log.Logger);
        bool browse = false;
        bool doubleTapped = false;

        var search = new AutoSuggestBox
        {
            PlaceholderText = Loc.Instance["AppPicker_Search"],
            QueryIcon = new SymbolIcon(Symbol.Find),
        };
        search.TextChanged += (sender, _) => viewModel.Query = sender.Text;

        var list = new ListView
        {
            Height = 360,
            SelectionMode = ListViewSelectionMode.Single,
            ItemsSource = viewModel.Apps,
            ItemTemplate = (DataTemplate)Application.Current.Resources["AppChoiceTemplate"],
        };
        list.SelectionChanged += (_, _) => viewModel.Selected = list.SelectedItem as AppChoice;

        var empty = new TextBlock { Text = Loc.Instance["AppPicker_Empty"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var loading = new ProgressRing { IsActive = true, HorizontalAlignment = HorizontalAlignment.Center };
        var browseButton = new HyperlinkButton { Content = Loc.WithoutAccessKey(Loc.Instance["AppPicker_BrowseFile"]) };

        var dialog = new ContentDialog
        {
            XamlRoot = App.XamlRoot,
            Title = Loc.Instance["App_Browse"],
            PrimaryButtonText = Loc.WithoutAccessKey(Loc.Instance["AppPicker_Choose"]),
            CloseButtonText = Loc.Instance["Common_Cancel"],
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            Content = new StackPanel { Width = 520, Spacing = 12, Children = { search, loading, list, empty, browseButton } },
        };
        browseButton.Click += (_, _) =>
        {
            browse = true;
            dialog.Hide();
        };
        list.DoubleTapped += (_, _) =>
        {
            if (viewModel.Selected is not null)
            {
                doubleTapped = true;
                dialog.Hide();
            }
        };
        viewModel.PropertyChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = viewModel.CanChoose;
            loading.Visibility = viewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
            empty.Visibility = viewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        };

        Task load = viewModel.LoadAsync();
        ContentDialogResult result = await dialog.ShowAsync();
        await load;

        if (browse)
        {
            return BrowseFile(currentPath);
        }

        if ((result == ContentDialogResult.Primary || doubleTapped) && viewModel.Selected is { } app)
        {
            Log.Information("App picker chose {File}", Path.GetFileName(app.Path));
            return new PickedApp(app.Path, app.Name);
        }

        return null;
    }

    private static PickedApp? BrowseFile(string? currentPath)
    {
        string current = Environment.ExpandEnvironmentVariables((currentPath ?? string.Empty).Trim().Trim('"'));
        string? folder = Path.IsPathFullyQualified(current) ? Path.GetDirectoryName(current) : null;
        string? file = NativeWindow.PickFile(App.WindowHandle, Loc.Instance["App_FileFilter"], Loc.Instance["App_Browse"], folder);
        if (file is null)
        {
            return null;
        }

        Log.Information("App picker chose file {File}", Path.GetFileName(file));
        return new PickedApp(file, null);
    }
}
