using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

public partial class ProfileEditorWindow : FluentWindow
{
    private const ModifierKeys RequiredModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows;

    private readonly ProfileEditorViewModel _viewModel;
    private bool _closeConfirmed;
    private bool _askingToDiscard;

    public ProfileEditorWindow(ProfileEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.CloseRequested += (_, saved) =>
        {
            if (saved)
            {
                _closeConfirmed = true;
                DialogResult = true;
            }
            else
            {
                Close();
            }
        };
        Closing += OnClosing;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>Cancel, Esc and the title bar's close button ask before unsaved changes are lost (analysis finding I-11).</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || (!_askingToDiscard && !_viewModel.HasChanges))
        {
            return;
        }

        e.Cancel = true;
        if (!_askingToDiscard)
        {
            _ = AskToDiscardAsync();
        }
    }

    private async Task AskToDiscardAsync()
    {
        _askingToDiscard = true;
        try
        {
            if (await ProfileDialogs.ConfirmDiscardAsync(this))
            {
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _askingToDiscard = false;
        }
    }

    private void OnBrowseApp(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement { DataContext: AppEditItem item })
        {
            return;
        }

        if (AppPickerWindow.Pick(this, item.Path) is { } path)
        {
            item.Path = path;
        }
    }

    /// <summary>A new app entry starts with the picker; cancelling it adds nothing (finding HW-11).</summary>
    private void OnAddApp(object sender, System.Windows.RoutedEventArgs e)
    {
        if (AppPickerWindow.Pick(this, null) is { } path)
        {
            _viewModel.AddApp(path);
        }
    }

    /// <summary>
    /// Records the pressed combination. Without Ctrl, Alt or Win, Tab, Esc and Enter keep their usual meaning (keyboard
    /// navigation, cancel, save) and Backspace/Delete clear the field.
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
        if (virtualKey != 0 && !Core.Profiles.Hotkey.IsModifierKey(virtualKey))
        {
            _viewModel.RecordHotkey(HotkeyFormat.FromWpf(modifiers), virtualKey);
        }
    }
}
