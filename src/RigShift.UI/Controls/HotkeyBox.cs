using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RigShift.App.ViewModels;
using RigShift.Core.Profiles;
using Windows.System;
using Windows.UI.Core;

namespace RigShift.UI.Controls;

/// <summary>
/// A read-only text box that records a key combination: focus starts recording, keys with Ctrl, Alt or Win become the
/// hotkey, Backspace or Delete alone clears it, Tab, Esc and Enter alone keep their usual meaning.
/// </summary>
public sealed partial class HotkeyBox : TextBox
{
    public static readonly DependencyProperty FieldProperty = DependencyProperty.Register(
        nameof(Field), typeof(IHotkeyField), typeof(HotkeyBox), new PropertyMetadata(null));

    /// <summary>The field recording began on; it ends there, also when another editor took the box meanwhile.</summary>
    private IHotkeyField? _recording;

    public HotkeyBox()
    {
        IsReadOnly = true;
        GotFocus += (_, _) =>
        {
            if (Field is { } field && _recording is null)
            {
                _recording = field;
                field.BeginHotkeyRecording();
            }
        };
        LostFocus += (_, _) =>
        {
            _recording?.EndHotkeyRecording();
            _recording = null;
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public IHotkeyField? Field
    {
        get => (IHotkeyField?)GetValue(FieldProperty);
        set => SetValue(FieldProperty, value);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Field is not { } field)
        {
            return;
        }

        HotkeyModifiers modifiers =
            (IsDown(VirtualKey.Menu) ? HotkeyModifiers.Alt : 0)
            | (IsDown(VirtualKey.Control) ? HotkeyModifiers.Control : 0)
            | (IsDown(VirtualKey.Shift) ? HotkeyModifiers.Shift : 0)
            | (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows) ? HotkeyModifiers.Windows : 0);
        bool plain = (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) == 0;

        if (plain && e.Key is VirtualKey.Tab or VirtualKey.Escape or VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (plain && e.Key is VirtualKey.Back or VirtualKey.Delete)
        {
            field.ClearHotkey();
            return;
        }

        int virtualKey = (int)e.Key;
        if (!Hotkey.IsModifierKey(virtualKey))
        {
            field.RecordHotkey(modifiers, virtualKey);
        }
    }
}
