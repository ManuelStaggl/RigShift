using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Win32.Input;
using RigShift.App.ViewModels;
using RigShift.Core.Profiles;

namespace RigShift.Avalonia.Views;

/// <summary>
/// A read-only text box that records a key combination: focus starts recording, keys with Ctrl, Alt or Win become the
/// hotkey, Backspace or Delete alone clears it, Tab, Esc and Enter alone keep their usual meaning.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly StyledProperty<IHotkeyField?> FieldProperty =
        AvaloniaProperty.Register<HotkeyBox, IHotkeyField?>(nameof(Field));

    /// <summary>The field recording began on; it ends there, also when another editor took the box meanwhile.</summary>
    private IHotkeyField? _recording;

    public HotkeyBox()
    {
        IsReadOnly = true;
        // Tunnel: the text box would take Backspace, Delete and the arrows for itself.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    public IHotkeyField? Field
    {
        get => GetValue(FieldProperty);
        set => SetValue(FieldProperty, value);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (Field is { } field && _recording is null)
        {
            _recording = field;
            field.BeginHotkeyRecording();
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _recording?.EndHotkeyRecording();
        _recording = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Field is not { } field)
        {
            return;
        }

        KeyModifiers keys = e.KeyModifiers;
        HotkeyModifiers modifiers =
            (keys.HasFlag(KeyModifiers.Alt) ? HotkeyModifiers.Alt : 0)
            | (keys.HasFlag(KeyModifiers.Control) ? HotkeyModifiers.Control : 0)
            | (keys.HasFlag(KeyModifiers.Shift) ? HotkeyModifiers.Shift : 0)
            | (keys.HasFlag(KeyModifiers.Meta) ? HotkeyModifiers.Windows : 0);
        bool plain = (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) == 0;

        if (plain && e.Key is Key.Tab or Key.Escape or Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (plain && e.Key is Key.Back or Key.Delete)
        {
            field.ClearHotkey();
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(e.Key);
        if (virtualKey != 0 && !Hotkey.IsModifierKey(virtualKey))
        {
            field.RecordHotkey(modifiers, virtualKey);
        }
    }
}
