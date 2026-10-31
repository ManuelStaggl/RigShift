using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Windows.Apps;
using RigShift.Windows.Ui;
using Serilog;

namespace RigShift.Avalonia.Views;

/// <summary>Picks a program for a profile or game: a Start menu or running app from the list, or any file.</summary>
public sealed partial class AppPickerWindow : Window
{
    private PickedApp? _picked;
    private bool _browse;

    public AppPickerWindow()
    {
        InitializeComponent();
        TextScale.Apply(this);
        DialogChrome.Apply(this);
    }

    private AppPickerViewModel ViewModel => (AppPickerViewModel)DataContext!;

    /// <summary>The <see cref="IAppPicker"/> of the Avalonia app: the list first, the "Open" dialog on request.</summary>
    internal static Task<PickedApp?> PickAsync(string? currentPath) => PickAsync(currentPath, () => AppDiscovery.Find(Log.Logger));

    /// <param name="find">Where the list comes from; Debug previews pass a demo list.</param>
    internal static async Task<PickedApp?> PickAsync(string? currentPath, Func<IReadOnlyList<DiscoveredApp>> find)
    {
        var viewModel = new AppPickerViewModel(find, AppIcons.Load, Log.Logger);
        var window = new AppPickerWindow { DataContext = viewModel };
        window.Opened += (_, _) => window.Search.Focus();
        Task load = viewModel.LoadAsync();
        if (App.Owner is { } owner)
        {
            await window.ShowDialog(owner);
        }

        await load;
        return window._browse ? BrowseFile(currentPath) : window._picked;
    }

    private static PickedApp? BrowseFile(string? currentPath)
    {
        string current = Environment.ExpandEnvironmentVariables((currentPath ?? string.Empty).Trim().Trim('"'));
        string? folder = Path.IsPathFullyQualified(current) ? Path.GetDirectoryName(current) : null;
        string? file = NativeWindow.PickFile(App.OwnerHandle, Loc.Instance["App_FileFilter"], Loc.Instance["App_Browse"], folder);
        if (file is null)
        {
            return null;
        }

        Log.Information("App picker chose file {File}", Path.GetFileName(file));
        return new PickedApp(file, null);
    }

    private void Choose()
    {
        if (ViewModel.Selected is { } app)
        {
            Log.Information("App picker chose {File}", Path.GetFileName(app.Path));
            _picked = new PickedApp(app.Path, app.Name);
            Close();
        }
    }

    private void OnChoose(object? sender, RoutedEventArgs e) => Choose();

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => Choose();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnBrowse(object? sender, RoutedEventArgs e)
    {
        _browse = true;
        Close();
    }
}

/// <summary>The app picker the shared editors ask for a program.</summary>
internal sealed class AvaloniaAppPicker : IAppPicker
{
    public Task<PickedApp?> PickAsync(string? currentPath) => AppPickerWindow.PickAsync(currentPath);
}
