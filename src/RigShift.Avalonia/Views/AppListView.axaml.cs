using Avalonia.Controls;
using Avalonia.Interactivity;
using RigShift.App.ViewModels;

namespace RigShift.Avalonia.Views;

/// <summary>The app list of a profile or game editor, on its <see cref="AppListEditor"/>.</summary>
public sealed partial class AppListView : UserControl
{
    public AppListView()
    {
        InitializeComponent();
    }

    private AppListEditor? Editor => DataContext as AppListEditor;

    private void OnBrowse(object? sender, RoutedEventArgs e) => Editor?.BrowseCommand.Execute(TagOf(sender));

    private void OnMoveUp(object? sender, RoutedEventArgs e) => Editor?.MoveUpCommand.Execute(TagOf(sender));

    private void OnMoveDown(object? sender, RoutedEventArgs e) => Editor?.MoveDownCommand.Execute(TagOf(sender));

    private void OnRemoveApp(object? sender, RoutedEventArgs e) => Editor?.RemoveCommand.Execute(TagOf(sender));

    private static AppEditItem? TagOf(object? sender) => (sender as Control)?.Tag as AppEditItem;
}
