using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.UI.Services;

namespace RigShift.UI.Pages;

/// <summary>Games prototype A: list and the session as steps on a rail (#93).</summary>
public sealed partial class GamesFlowPage : Page
{
    public GamesFlowPage()
    {
        Catalog = App.Provider.GetRequiredService<GameCatalog>();
        InitializeComponent();
        Loaded += (_, _) => List.SelectedIndex = Catalog.Items.Count > 0 ? 0 : -1;
    }

    public GameCatalog Catalog { get; }

    private void OnSelected(object sender, SelectionChangedEventArgs e) => GamesPrototype.ShowHead(List.SelectedItem as GameItem, HeadName, HeadIcon, HeadGlyph);
}

/// <summary>What both games prototypes share until one is chosen.</summary>
internal static class GamesPrototype
{
    public static void ShowHead(GameItem? item, TextBlock name, Image icon, FontIcon glyph)
    {
        if (item is null)
        {
            return;
        }

        name.Text = item.Name;
        icon.Source = WinUiIcons.Image(item.GameIcon);
        icon.Visibility = item.HasGameIcon ? Visibility.Visible : Visibility.Collapsed;
        glyph.Glyph = ProfileGlyphs.ForGame(item.IconKey);
        glyph.Visibility = item.HasGameIcon ? Visibility.Collapsed : Visibility.Visible;
    }
}
