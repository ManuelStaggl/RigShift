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
        // The handlers look the field up when they run, so a new editor behind the same box needs no new handlers.
        if (d is not TextBox box || e.OldValue is not null || e.NewValue is null)
        {
            return;
        }

        box.GotKeyboardFocus += (_, _) => GetField(box)?.BeginHotkeyRecording();
        box.LostKeyboardFocus += (_, _) => GetField(box)?.EndHotkeyRecording();
        box.PreviewKeyDown += OnPreviewKeyDown;
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
