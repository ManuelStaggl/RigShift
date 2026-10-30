using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace RigShift.UI;

/// <summary>A page's name as its heading; stands in for the pages until they are built.</summary>
public sealed partial class PlaceholderPage : Page
{
    public PlaceholderPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Heading.Text = e.Parameter as string ?? string.Empty;
    }
}
