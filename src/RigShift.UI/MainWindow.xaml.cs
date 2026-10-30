using Microsoft.UI.Xaml;
using RigShift.App.Localization;

namespace RigShift.UI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Texts are read once per load ({l:Tr}); a new language takes a new shell. Loc raises this on the UI thread.
        Loc.Instance.PropertyChanged += (_, _) => Content = new Shell();
    }
}
