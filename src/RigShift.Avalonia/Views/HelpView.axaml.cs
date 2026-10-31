using Avalonia.Controls;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.ViewModels;

namespace RigShift.Avalonia.Views;

/// <summary>
/// The help page on <see cref="AboutViewModel"/>: version and updates on top, then first steps, the hotkeys and
/// commands in use, backup and troubleshooting as four cards, the links at the foot.
/// </summary>
public sealed partial class HelpView : UserControl
{
    public HelpView()
    {
        ViewModel = App.Provider.GetRequiredService<AboutViewModel>();
        DataContext = ViewModel;
        InitializeComponent();
        ShowMark();
        ActualThemeVariantChanged += (_, _) => ShowMark();
    }

    public AboutViewModel ViewModel { get; }

    /// <summary>The brand package draws the mark once for dark and once for light surfaces.</summary>
    private void ShowMark()
    {
        bool light = ActualThemeVariant == ThemeVariant.Light;
        MarkLight.IsVisible = light;
        MarkDark.IsVisible = !light;
    }
}
