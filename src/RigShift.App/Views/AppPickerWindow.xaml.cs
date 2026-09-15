using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.Windows.Apps;
using Serilog;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>Result of the picker: the path and, for an installed or running app, its display name.</summary>
public sealed record PickedApp(string Path, string? Name);

/// <summary>Picks a program for a profile: installed and running apps, or any file (finding HW-11).</summary>
public partial class AppPickerWindow : FluentWindow
{
    private readonly AppPickerViewModel _viewModel;
    private readonly string? _currentPath;

    private AppPickerWindow(AppPickerViewModel viewModel, string? currentPath)
    {
        _viewModel = viewModel;
        _currentPath = currentPath;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await viewModel.LoadAsync();
        };
    }

    public PickedApp? Chosen { get; private set; }

    /// <returns>The chosen program, or <c>null</c> if cancelled.</returns>
    public static PickedApp? Pick(Window? owner, string? currentPath) =>
        Pick(owner, currentPath, () => AppDiscovery.Find(Log.Logger));

    /// <param name="find">The programs to offer; a demo list for README screenshots in debug builds.</param>
    internal static PickedApp? Pick(Window? owner, string? currentPath, Func<IReadOnlyList<DiscoveredApp>> find)
    {
        var viewModel = new AppPickerViewModel(find, AppIcons.Load, Log.Logger);
        var window = new AppPickerWindow(viewModel, currentPath);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return window.ShowDialog() == true ? window.Chosen : null;
    }

    private void Choose(string path, string? name)
    {
        Chosen = new PickedApp(path, name);
        Log.Information("App picker chose {File}", Path.GetFileName(path));
        DialogResult = true;
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected is { } app)
        {
            Choose(app.Path, app.Name);
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(AppList, source) is ListBoxItem { DataContext: AppChoice app })
        {
            Choose(app.Path, app.Name);
        }
    }

    /// <summary>Arrow down leaves the search for the list, on the selected (or first) app.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || _viewModel.Apps.Count == 0)
        {
            return;
        }

        e.Handled = true;
        _viewModel.Selected ??= _viewModel.Apps[0];
        AppList.ScrollIntoView(_viewModel.Selected);
        if (AppList.ItemContainerGenerator.ContainerFromItem(_viewModel.Selected) is ListBoxItem item)
        {
            item.Focus();
        }
    }

    /// <summary>Programs without a Start menu entry, e.g. a tool unpacked into a folder.</summary>
    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.Instance["App_FileFilter"],
            Title = Loc.Instance["App_Browse"],
        };

        string current = Environment.ExpandEnvironmentVariables((_currentPath ?? string.Empty).Trim().Trim('"'));
        if (Path.IsPathFullyQualified(current) && Path.GetDirectoryName(current) is { } folder && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            Choose(dialog.FileName, null);
        }
    }
}

/// <summary>Program icons for the picker, frozen so they can be made off the UI thread.</summary>
internal static class AppIcons
{
    public static ImageSource? Load(string path)
    {
        try
        {
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception or COMException)
        {
            Log.Debug("No icon for {File}: {Reason}", Path.GetFileName(path), ex.Message);
            return null;
        }
    }
}
