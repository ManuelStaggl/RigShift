using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RigShift.App.Services;
using RigShift.App.ViewModels;

namespace RigShift.App.Views.Pages;

public partial class ProfilesPage : Page
{
    private const ModifierKeys RequiredModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows;

    private readonly ProfilesViewModel _viewModel;

    public ProfilesPage(ProfilesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.FocusNameRequested += (_, _) => FocusName();
        PreviewKeyDown += OnPagePreviewKeyDown;
    }

    /// <summary>F2 edits the name (R-NAV-5).</summary>
    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None && _viewModel.HasSelection)
        {
            e.Handled = true;
            FocusName();
        }
    }

    private void FocusName()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnNewClick(object sender, RoutedEventArgs e) => Controls.MenuButton.Open((FrameworkElement)sender, _viewModel, alignRight: true);

    private void OnMoreClick(object sender, RoutedEventArgs e) => Controls.MenuButton.Open((FrameworkElement)sender, _viewModel, alignRight: true);

    private void OnIconClick(object sender, RoutedEventArgs e) => Controls.MenuButton.Open((FrameworkElement)sender, _viewModel.Editor);

    private void OnAddDeviceClick(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        Controls.MenuButton.Open(button, button.DataContext);
    }

    /// <summary>A new app entry starts with the picker; cancelling it adds nothing (finding HW-11).</summary>
    private void OnAddApp(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Editor is { } editor && AppPickerWindow.Pick(Window.GetWindow(this), null) is { } picked)
        {
            editor.AddApp(picked.Path, picked.Name);
        }
    }

    private void OnBrowseApp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AppEditItem item } && AppPickerWindow.Pick(Window.GetWindow(this), item.Path) is { } picked)
        {
            item.SetPicked(picked.Path, picked.Name);
        }
    }

    private void OnHotkeyGotFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.Editor?.BeginHotkeyRecording();

    private void OnHotkeyLostFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.Editor?.EndHotkeyRecording();

    /// <summary>
    /// Records the pressed combination. Without Ctrl, Alt or Win, Tab, Esc and Enter keep their usual meaning and
    /// Backspace/Delete clear the field.
    /// </summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.Editor is not { } editor)
        {
            return;
        }

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
            editor.ClearHotkeyCommand.Execute(null);
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey != 0 && !Core.Profiles.Hotkey.IsModifierKey(virtualKey))
        {
            editor.RecordHotkey(HotkeyFormat.FromWpf(modifiers), virtualKey);
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

    private async void OnDeviceNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UsbNameCard card })
        {
            await card.SaveNameAsync();
        }
    }

    /// <summary>Enter saves, Esc puts the saved name back (F6).</summary>
    private async void OnDeviceNameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UsbNameCard card })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await card.SaveNameAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            card.CustomName = card.SavedName ?? string.Empty;
        }
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

public partial class OverviewPage : Page
{
    public OverviewPage(OverviewViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.RefreshCommand.Execute(null);
    }

    private async void OnNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DisplayCard card })
        {
            await card.SaveNameAsync();
        }
    }

    /// <summary>Enter saves, Esc puts the saved name back (F6).</summary>
    private async void OnNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DisplayCard card })
        {
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            await card.SaveNameAsync();
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            card.CustomName = card.SavedName ?? string.Empty;
        }
    }
}

/// <summary>The field of view page; the displays are read when the page is shown.</summary>
public partial class FovPage : Page
{
    public FovPage(FovViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.RefreshCommand.Execute(null);
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

public partial class GamesPage : Page
{
    private const ModifierKeys RequiredModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows;

    private readonly GamesViewModel _viewModel;
    private readonly GameDialogs _dialogs;

    public GamesPage(GamesViewModel viewModel, GameDialogs dialogs)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(dialogs);
        _viewModel = viewModel;
        _dialogs = dialogs;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.FocusNameRequested += (_, _) => FocusName();
        PreviewKeyDown += OnPagePreviewKeyDown;
    }

    /// <summary>F2 edits the name (R-NAV-5).</summary>
    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None && _viewModel.HasSelection)
        {
            e.Handled = true;
            FocusName();
        }
    }

    private void FocusName()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnNewClick(object sender, RoutedEventArgs e) => Controls.MenuButton.Open((FrameworkElement)sender, _viewModel, alignRight: true);

    private void OnMoreClick(object sender, RoutedEventArgs e) => Controls.MenuButton.Open((FrameworkElement)sender, _viewModel, alignRight: true);

    private void OnIconClick(object sender, RoutedEventArgs e) => Controls.MenuButton.Open((FrameworkElement)sender, _viewModel.Editor);

    /// <summary>"Choose…" on the Game tab: an installed game or a program.</summary>
    private async void OnPickGame(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Editor is not { } editor || await _dialogs.PickGameAsync() is not { } picked)
        {
            return;
        }

        if (picked.Installed is { } installed)
        {
            editor.SetLaunch(installed);
        }
        else if (picked.ExecutablePath is { } path)
        {
            editor.SetExecutable(path);
        }
    }

    private void OnCaptureWindows(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Editor is { } editor && WindowCaptureWindow.Capture(Window.GetWindow(this), _dialogs, editor.WindowLayout) is { } captured)
        {
            editor.WindowLayout = captured;
        }
    }

    /// <summary>A new tool starts with the picker; cancelling it adds nothing (finding HW-11).</summary>
    private void OnAddApp(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Editor is { } editor && AppPickerWindow.Pick(Window.GetWindow(this), null) is { } picked)
        {
            editor.AddApp(picked.Path, picked.Name);
        }
    }

    private void OnBrowseApp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AppEditItem item } && AppPickerWindow.Pick(Window.GetWindow(this), item.Path) is { } picked)
        {
            item.SetPicked(picked.Path, picked.Name);
        }
    }

    private void OnHotkeyGotFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.Editor?.BeginHotkeyRecording();

    private void OnHotkeyLostFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.Editor?.EndHotkeyRecording();

    /// <summary>Same rules as the profile editor: Tab, Esc and Enter keep their meaning, Backspace/Delete clear the field.</summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.Editor is not { } editor)
        {
            return;
        }

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
            editor.ClearHotkeyCommand.Execute(null);
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey != 0 && !Core.Profiles.Hotkey.IsModifierKey(virtualKey))
        {
            editor.RecordHotkey(HotkeyFormat.FromWpf(modifiers), virtualKey);
        }
    }
}
