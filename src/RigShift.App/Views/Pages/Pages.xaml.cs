using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RigShift.App.Services;
using RigShift.App.ViewModels;

namespace RigShift.App.Views.Pages;

public partial class ProfilesPage : Page
{
    public ProfilesPage(ProfilesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>The "more" button opens its context menu on click and Enter, not only on right click.</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.DataContext = button.DataContext;
            menu.IsOpen = true;
        }
    }
}

public partial class SettingsPage : Page
{
    private const ModifierKeys RequiredModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows;

    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.Load();
    }

    private void OnToggleHotkeyGotFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.BeginHotkeyRecording();

    private void OnToggleHotkeyLostFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.EndHotkeyRecording();

    /// <summary>Same rules as the profile editor: Tab and Esc keep their meaning, Backspace/Delete clear the field.</summary>
    private void OnToggleHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool plain = (modifiers & RequiredModifiers) == ModifierKeys.None;

        if (plain && key is Key.Tab or Key.Escape or Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (plain && key is Key.Back or Key.Delete)
        {
            _viewModel.ClearToggleHotkeyCommand.Execute(null);
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey != 0 && !Core.Profiles.Hotkey.IsModifierKey(virtualKey))
        {
            _viewModel.RecordToggleHotkey(HotkeyFormat.FromWpf(modifiers), virtualKey);
        }
    }
}

public partial class DisplaysPage : Page
{
    public DisplaysPage(DisplaysViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.RefreshCommand.Execute(null);

        // The cards' state and "in profiles" texts are built in code; rebuild them on a language change (I-13).
        Localization.Loc.Instance.PropertyChanged += (_, _) =>
        {
            if (IsLoaded)
            {
                viewModel.RefreshCommand.Execute(null);
            }
        };
    }

    private async void OnNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DisplayCard card })
        {
            await card.SaveNameAsync();
        }
    }

    private async void OnNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is FrameworkElement { DataContext: DisplayCard card })
        {
            e.Handled = true;
            await card.SaveNameAsync();
        }
    }
}

public partial class AutomationPage : Page
{
    public AutomationPage(AutomationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.Load();
    }

    private async void OnNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UsbNameCard card })
        {
            await card.SaveNameAsync();
        }
    }

    private async void OnNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is FrameworkElement { DataContext: UsbNameCard card })
        {
            e.Handled = true;
            await card.SaveNameAsync();
        }
    }
}

public partial class AboutPage : Page
{
    public AboutPage(AboutViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
