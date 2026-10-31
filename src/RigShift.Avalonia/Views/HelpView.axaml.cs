using Avalonia.Controls;
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
    }

    public AboutViewModel ViewModel { get; }
}
