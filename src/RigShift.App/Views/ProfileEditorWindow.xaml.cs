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

    public ProfileEditorWindow(ProfileEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.CloseRequested += (_, saved) => DialogResult = saved;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnBrowseApp(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement { DataContext: AppEditItem item })
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Localization.Loc.Instance["App_FileFilter"],
            Title = Localization.Loc.Instance["App_Browse"],
        };

        string current = Environment.ExpandEnvironmentVariables(item.Path.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(current) && Path.GetDirectoryName(current) is { } folder && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            item.Path = dialog.FileName;
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
