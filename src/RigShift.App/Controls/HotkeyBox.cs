using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RigShift.App.Services;
using RigShift.App.ViewModels;

namespace RigShift.App.Controls;

/// <summary>
/// Turns a read-only text box into a hotkey field: <c>controls:HotkeyBox.Field="{Binding}"</c>. One place for what the
/// profile editor, the game editor and the settings did three times in code-behind (v4 finding A-07). Without Ctrl, Alt
/// or Win, Tab, Esc and Enter keep their usual meaning and Backspace/Delete clear the field.
/// </summary>
public static class HotkeyBox
{
    private const ModifierKeys RequiredModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows;

    public static readonly DependencyProperty FieldProperty = DependencyProperty.RegisterAttached(
        "Field", typeof(IHotkeyField), typeof(HotkeyBox), new PropertyMetadata(null, OnFieldChanged));

    /// <summary>The field recording began on; it ends there, also when another editor took the box meanwhile.</summary>
    private static readonly DependencyProperty RecordingProperty = DependencyProperty.RegisterAttached(
        "Recording", typeof(IHotkeyField), typeof(HotkeyBox), new PropertyMetadata(null));

    public static IHotkeyField? GetField(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (IHotkeyField?)element.GetValue(FieldProperty);
    }

    public static void SetField(DependencyObject element, IHotkeyField? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(FieldProperty, value);
    }

    private static void OnFieldChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        // The handlers look the field up when they run, so a new editor behind the same box needs no new handlers.
        // Removing first keeps them single when the field goes to null and back (no selection, then a new one).
        box.GotKeyboardFocus -= OnGotKeyboardFocus;
        box.LostKeyboardFocus -= OnLostKeyboardFocus;
        box.PreviewKeyDown -= OnPreviewKeyDown;
        box.GotKeyboardFocus += OnGotKeyboardFocus;
        box.LostKeyboardFocus += OnLostKeyboardFocus;
        box.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && GetField(box) is { } field && box.GetValue(RecordingProperty) is null)
        {
            box.SetValue(RecordingProperty, field);
            field.BeginHotkeyRecording();
        }
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && box.GetValue(RecordingProperty) is IHotkeyField field)
        {
            box.ClearValue(RecordingProperty);
            field.EndHotkeyRecording();
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || GetField(box) is not { } field)
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
            field.ClearHotkey();
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey != 0 && !Core.Profiles.Hotkey.IsModifierKey(virtualKey))
        {
            field.RecordHotkey(HotkeyFormat.FromWpf(modifiers), virtualKey);
        }
    }
}
