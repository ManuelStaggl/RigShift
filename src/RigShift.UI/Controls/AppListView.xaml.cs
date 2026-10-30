using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RigShift.App.ViewModels;

namespace RigShift.UI.Controls;

/// <summary>The app list of a profile or game editor; the same view for both.</summary>
public sealed partial class AppListView : UserControl
{
    public static readonly DependencyProperty ListProperty = DependencyProperty.Register(
        nameof(List), typeof(AppListEditor), typeof(AppListView), new PropertyMetadata(null, (d, _) => ((AppListView)d).Bindings.Update()));

    public AppListView()
    {
        InitializeComponent();
    }

    public AppListEditor? List
    {
        get => (AppListEditor?)GetValue(ListProperty);
        set => SetValue(ListProperty, value);
    }

    private static AppEditItem? ItemOf(object sender) => ((FrameworkElement)sender).Tag as AppEditItem;

    private void OnBrowse(object sender, RoutedEventArgs e) => List?.BrowseCommand.Execute(ItemOf(sender));

    private void OnMoveUp(object sender, RoutedEventArgs e) => List?.MoveUpCommand.Execute(ItemOf(sender));

    private void OnMoveDown(object sender, RoutedEventArgs e) => List?.MoveDownCommand.Execute(ItemOf(sender));

    private void OnRemove(object sender, RoutedEventArgs e) => List?.RemoveCommand.Execute(ItemOf(sender));

    /// <summary>A new list clears the selection for a moment; writing that null back would change the entry.</summary>
    private void SetWaitDevice(object value)
    {
        if (value is Choice choice && List is { } list)
        {
            list.WaitDevice.Selected = choice;
        }
    }

    private void OnWaitChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (sender.Tag is AppEditItem item)
        {
            item.WaitSeconds = Services.Bindings.FromNumber(args.NewValue);
        }
    }
}
