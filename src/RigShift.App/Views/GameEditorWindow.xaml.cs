using System.Windows;
using System.Windows.Input;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Profiles;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>The game editor, in the order a session runs: the game, the profile, the apps, the windows, the end.</summary>
public partial class GameEditorWindow : FluentWindow
{
    private const ModifierKeys RequiredModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows;

    private readonly GameEditorViewModel _viewModel;
    private readonly GameDialogs _dialogs;

    public GameEditorWindow(GameEditorViewModel viewModel, GameDialogs dialogs)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(dialogs);

        _viewModel = viewModel;
        _dialogs = dialogs;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CanSave)
        {
            DialogResult = true;
        }
    }

    private async void OnPickGame(object sender, RoutedEventArgs e)
    {
        if (await GamePickerWindow.PickAsync(this, _dialogs) is not { } picked)
        {
            return;
        }

        if (picked.Installed is { } installed)
        {
            _viewModel.SetLaunch(installed);
        }
        else if (picked.ExecutablePath is { } path)
        {
            _viewModel.SetExecutable(path);
        }
    }

    private void OnCaptureWindows(object sender, RoutedEventArgs e)
    {
        if (WindowCaptureWindow.Capture(this, _dialogs, _viewModel.WindowLayout) is { } captured)
        {
            _viewModel.WindowLayout = captured;
        }
    }

    private void OnBrowseApp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AppEditItem item } && AppPickerWindow.Pick(this, item.Path) is { } picked)
        {
            item.SetPicked(picked.Path, picked.Name);
        }
    }

    /// <summary>A new app entry starts with the picker; cancelling it adds nothing.</summary>
    private void OnAddApp(object sender, RoutedEventArgs e)
    {
        if (AppPickerWindow.Pick(this, null) is { } picked)
        {
            _viewModel.AddApp(picked.Path, picked.Name);
        }
    }

    /// <summary>While the field has focus, a combination is recorded rather than acted on.</summary>
    private void OnHotkeyGotFocus(object sender, KeyboardFocusChangedEventArgs e) => HotkeyBox.SelectAll();

    private void OnHotkeyLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Nothing to undo: the combination is kept as soon as it is pressed.
    }

    /// <summary>
    /// Records the pressed combination. Without Ctrl, Alt or Win, Tab, Esc and Enter keep their usual meaning
    /// (keyboard navigation, cancel, save) and Backspace/Delete clear the field.
    /// </summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
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
            _viewModel.ClearHotkeyCommand.Execute(null);
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey != 0 && !Hotkey.IsModifierKey(virtualKey))
        {
            _viewModel.RecordHotkey(HotkeyFormat.FromWpf(modifiers), virtualKey);
        }
    }
}
