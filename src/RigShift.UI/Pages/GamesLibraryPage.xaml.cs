using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using RigShift.App.Services;
using RigShift.App.ViewModels;

namespace RigShift.UI.Pages;

/// <summary>Games prototype B: game tiles and a sheet with sections below (#93).</summary>
public sealed partial class GamesLibraryPage : Page
{
    public GamesLibraryPage()
    {
        Catalog = App.Provider.GetRequiredService<GameCatalog>();
        InitializeComponent();
        Loaded += (_, _) => Tiles.SelectedIndex = Catalog.Items.Count > 0 ? 0 : -1;
    }

    public GameCatalog Catalog { get; }

    private void OnSelected(object sender, SelectionChangedEventArgs e) => GamesPrototype.ShowHead(Tiles.SelectedItem as GameItem, HeadName, HeadIcon, HeadGlyph);
}
