using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.UI.Services;

namespace RigShift.UI.Pages;

/// <summary>Profiles prototype A: list and a detail with one tab per area (#92).</summary>
public sealed partial class ProfilesTabsPage : Page
{
    public ProfilesTabsPage()
    {
        Catalog = App.Provider.GetRequiredService<ProfileCatalog>();
        InitializeComponent();
        Loaded += (_, _) =>
        {
            List.SelectedIndex = ProfilesPrototype.InitialIndex(Catalog);
            if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_TAB") is { } tab)
            {
                Tabs.SelectedItem = Tabs.Items.FirstOrDefault(i => (string)i.Tag == tab);
            }
        };
    }

    public ProfileCatalog Catalog { get; }

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not ProfileItem item)
        {
            return;
        }

        HeadSymbol.Data = ProfileGlyphs.Drawing(item.IconKey);
        HeadName.Text = item.Name;
        HeadLine.Text = ProfilesPrototype.HeadLine(item);
        Picture.Displays = item.Topology;
        Picture.SelectedKey = item.Topology.Count > 0 ? item.Topology[0].Key : null;
    }

    private void OnTab(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        string? tab = sender.SelectedItem?.Tag as string;
        DisplaysTab.Visibility = tab is null or "Displays" ? Visibility.Visible : Visibility.Collapsed;
        AppsTab.Visibility = tab == "Apps" ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>What both profile prototypes share until one is chosen.</summary>
internal static class ProfilesPrototype
{
    /// <summary>The second profile when there is one: the demo's Sim Rig shows more than the desk.</summary>
    public static int InitialIndex(ProfileCatalog catalog)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_ACTIVE") is { } name && catalog.Items.FirstOrDefault(i => i.Name == name) is { } active)
        {
            active.IsActive = true;
        }
#endif
        return catalog.Items.Count > 1 ? 1 : 0;
    }

    public static string HeadLine(ProfileItem item) =>
        string.Join(" · ", new[] { item.IsActive ? RigShift.App.Localization.Loc.Instance["Profile_Active"] : item.ReadyText, item.HotkeyText }.Where(t => !string.IsNullOrEmpty(t)));
}
