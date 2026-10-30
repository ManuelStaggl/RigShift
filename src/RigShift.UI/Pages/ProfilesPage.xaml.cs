using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.UI.Services;
using Windows.System;

namespace RigShift.UI.Pages;

/// <summary>
/// The profiles page as one sheet per profile. The editor lives in <see cref="ProfilesViewModel"/>; this page adds what
/// only a view can: the icon menu, the device menus of USB rules and the one-line summaries of the closed sections.
/// </summary>
public sealed partial class ProfilesPage : Page
{
    private ProfileEditorViewModel? _editor;

    public ProfilesPage()
    {
        ViewModel = App.Provider.GetRequiredService<ProfilesViewModel>();
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged += OnViewModelChanged;
            ViewModel.FocusNameRequested += OnFocusNameRequested;
            ViewModel.PageShown();
            Attach(ViewModel.Editor);
            ViewModel.Editor?.RefreshAutostartNote();
        };
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            ViewModel.FocusNameRequested -= OnFocusNameRequested;
            Attach(null);
        };
        KeyDown += OnKeyDown;
    }

    public ProfilesViewModel ViewModel { get; }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfilesViewModel.Editor))
        {
            Attach(ViewModel.Editor);
        }
    }

    /// <summary>A new editor behind the sheet: its icon menu and summaries follow it.</summary>
    private void Attach(ProfileEditorViewModel? editor)
    {
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorChanged;
        }

        _editor = editor;
        if (editor is null)
        {
            return;
        }

        editor.PropertyChanged += OnEditorChanged;
        IconMenu.Items.Clear();
        foreach (Choice icon in editor.IconChoices)
        {
            var item = new MenuFlyoutItem { Text = icon.Name, Icon = new PathIcon { Data = ProfileGlyphs.Drawing(icon.Key) } };
            item.Click += (_, _) => editor.ChooseIconCommand.Execute(icon);
            IconMenu.Items.Add(item);
        }

        ShowSummaries();
        ShowDisplayChoices();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        ShowSummaries();
        if (e.PropertyName == nameof(ProfileEditorViewModel.SelectedDisplay))
        {
            ShowDisplayChoices();
        }
    }

    /// <summary>
    /// Rate and HDR of the chosen display, set again once its lists are in: the bindings may hand over the selection
    /// before the new list, and the box then shows nothing.
    /// </summary>
    private void ShowDisplayChoices() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_editor?.SelectedDisplay is { } display)
        {
            RefreshBox.SelectedItem = display.SelectedRefresh;
            HdrBox.SelectedItem = display.SelectedHdr;
        }
    });

    /// <summary>What each closed section holds, in one line: the sheet reads like the profile without opening anything.</summary>
    private void ShowSummaries()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        IconPath.Data = ProfileGlyphs.Drawing(editor.SelectedIcon?.Key);
        AudioSection.Description = Join(editor.AudioSlots.Where(s => s.HasDevice).Select(s => s.SetVolume ? $"{s.Selected?.Name} · {s.VolumeText}" : s.Selected?.Name));
        AppsSection.Description = Join(editor.AppList.Items.Select(a => a.DisplayName));
        TriggersSection.Description = Join(editor.Rules.Rules.Select(r => r.DevicesText).Append(editor.HotkeyText));
        BehaviorSection.Description = Join([
            editor.KeepAwake ? Loc.Instance["Editor_KeepAwake"] : null,
            editor.RememberWindows ? Loc.Instance["Editor_RememberWindows"] : null,
            editor.SwitchWithoutAsking ? Loc.Instance["Editor_SwitchWithoutAsking"] : null,
        ]);
    }

    private static string Join(IEnumerable<string?> parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>F2 edits the name.</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F2 && ViewModel.HasSelection)
        {
            e.Handled = true;
            FocusName();
        }
    }

    private void OnFocusNameRequested(object? sender, EventArgs e) => FocusName();

    private void FocusName()
    {
        NameBox.Focus(FocusState.Programmatic);
        NameBox.SelectAll();
    }

    // BindBack for the selectors whose list changes with the editor or the display: a new list clears the selection for a
    // moment, and writing that null back would change the profile and mark it unsaved.
    private void SetSurround(object value)
    {
        if (value is Choice choice && _editor is { } editor)
        {
            editor.Surround.Selected = choice;
        }
    }

    private void SetRefresh(object value)
    {
        if (value is RefreshChoice choice && _editor?.SelectedDisplay is { } display)
        {
            display.SelectedRefresh = choice;
        }
    }

    private void SetHdr(object value)
    {
        if (value is HdrChoice choice && _editor?.SelectedDisplay is { } display)
        {
            display.SelectedHdr = choice;
        }
    }

    private void OnExitDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (sender.Tag is RuleCard card)
        {
            card.ExitDelaySeconds = Services.Bindings.FromNumber(args.NewValue);
        }
    }

    private void OnRemoveDisplay(object sender, RoutedEventArgs e) => _editor?.RemoveDisplayCommand.Execute(_editor.SelectedDisplay);

    private void OnRemoveRule(object sender, RoutedEventArgs e) => _editor?.Rules.RemoveRuleCommand.Execute(((FrameworkElement)sender).Tag as RuleCard);

    private void OnRemoveDevice(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is RuleDeviceSlot slot)
        {
            slot.Card.RemoveDeviceCommand.Execute(slot);
        }
    }

    /// <summary>The devices a rule does not have yet, built when its menu opens: the list changes as devices come and go.</summary>
    private void OnAddDeviceOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu || menu.Target is not FrameworkElement { Tag: RuleCard card })
        {
            return;
        }

        menu.Items.Clear();
        foreach (Choice device in card.AvailableDevices)
        {
            var item = new MenuFlyoutItem { Text = device.Name };
            item.Click += (_, _) => card.AddDeviceCommand.Execute(device);
            menu.Items.Add(item);
        }
    }
}
