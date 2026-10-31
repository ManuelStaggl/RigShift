using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.ViewModels;

namespace RigShift.Avalonia.Views;

/// <summary>
/// The field of view page on <see cref="FovViewModel"/>: the rig on the left, the top view and the results on the right,
/// one line per sim below. The displays are read whenever the page is shown.
/// </summary>
public sealed partial class FovView : UserControl
{
    public FovView()
    {
        ViewModel = App.Provider.GetRequiredService<FovViewModel>();
        DataContext = ViewModel;
        InitializeComponent();
        AttachedToVisualTree += (_, _) => ViewModel.RefreshCommand.Execute(null);
    }

    public FovViewModel ViewModel { get; }
}
