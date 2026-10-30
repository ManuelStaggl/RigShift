using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.UI.Services;

namespace RigShift.UI.Pages;

/// <summary>Profiles prototype B: chips on top, the profile as one sheet with sections that open in place (#92).</summary>
public sealed partial class ProfilesSheetPage : Page
{
    public ProfilesSheetPage()
    {
        Catalog = App.Provider.GetRequiredService<ProfileCatalog>();
        InitializeComponent();
        Loaded += (_, _) => Chips.SelectedIndex = ProfilesPrototype.InitialIndex(Catalog);
    }

    public ProfileCatalog Catalog { get; }

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Chips.SelectedItem is not ProfileItem item)
        {
            return;
        }

        HeadSymbol.Data = ProfileGlyphs.Drawing(item.IconKey);
        HeadName.Text = item.Name;
        HeadLine.Text = ProfilesPrototype.HeadLine(item);
        Picture.Displays = item.Topology;
        Picture.SelectedKey = item.Topology.Count > 0 ? item.Topology[0].Key : null;
    }
}
