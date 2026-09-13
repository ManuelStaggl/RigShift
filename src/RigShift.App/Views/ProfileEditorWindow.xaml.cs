using RigShift.App.ViewModels;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

public partial class ProfileEditorWindow : FluentWindow
{
    public ProfileEditorWindow(ProfileEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();

        viewModel.CloseRequested += (_, saved) => DialogResult = saved;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }
}
