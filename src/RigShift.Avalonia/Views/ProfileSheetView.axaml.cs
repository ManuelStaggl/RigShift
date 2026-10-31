using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using Ursa.Controls;

namespace RigShift.Avalonia.Views;

/// <summary>
/// The profiles page as one sheet per profile, on <see cref="ProfilesViewModel"/> like the WinUI ProfilesPage. The view
/// adds what only a view can: the icon menu and the one-line summaries of the closed groups.
/// </summary>
public sealed partial class ProfileSheetView : UserControl
{
    private ProfileEditorViewModel? _editor;
    private bool _previewDone;

    public ProfileSheetView()
    {
        ViewModel = App.Provider.GetRequiredService<ProfilesViewModel>();
        DataContext = ViewModel;
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            ViewModel.PropertyChanged += OnViewModelChanged;
            ViewModel.FocusNameRequested += OnFocusNameRequested;
            PreviewSelect();
            ViewModel.PageShown();
            Attach(ViewModel.Editor);
            ViewModel.Editor?.RefreshAutostartNote();
        };
        DetachedFromVisualTree += (_, _) =>
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
        var iconMenu = (MenuFlyout)IconButton.Flyout!;
        iconMenu.Items.Clear();
        foreach (Choice icon in editor.IconChoices)
        {
            var item = new MenuItem
            {
                Header = icon.Name,
                Icon = new Path { Data = Glyphs.Profile(icon.Key), Classes = { "symbol" }, Width = 18, Height = 18 },
            };
            item.Click += (_, _) => editor.ChooseIconCommand.Execute(icon);
            iconMenu.Items.Add(item);
        }

        ShowSummaries();
        ShowDisplayChoices();
        PreviewExpand(editor);
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        ShowSummaries();
        if (e.PropertyName == nameof(ProfileEditorViewModel.SelectedDisplay))
        {
            ShowDisplayChoices();
        }
    }

    /// <summary>Rate and HDR of the chosen display, set again once its lists are in (the WinUI page does the same).</summary>
    private void ShowDisplayChoices() => Dispatcher.UIThread.Post(() =>
    {
        if (_editor?.SelectedDisplay is { } display)
        {
            RefreshBox.SelectedItem = display.SelectedRefresh;
            HdrBox.SelectedItem = display.SelectedHdr;
        }
    });

    /// <summary>What each closed group holds, in one line: the sheet reads like the profile without opening anything.</summary>
    private void ShowSummaries()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        IconPath.Data = Glyphs.Profile(editor.SelectedIcon?.Key);
        BarIcon.Data = IconPath.Data;
        ProfileItem? active = ViewModel.Items.FirstOrDefault(i => i.IsActive);
        ActiveCard.DataContext = active;
        ActiveCard.IsVisible = active is not null;
        AudioCard.Description = Join(editor.AudioSlots.Where(s => s.HasDevice).Select(s => s.SetVolume ? $"{s.Selected?.Name} · {s.VolumeText}" : s.Selected?.Name));
        AppsCard.Description = Join(editor.AppList.Items.Select(a => a.DisplayName));
        TriggersCard.Description = Join(editor.Rules.Rules.Select(r => r.DevicesText).Append(editor.HotkeyText));
        BehaviorCard.Description = Join([
            editor.KeepAwake ? Loc.Instance["Editor_KeepAwake"] : null,
            editor.RememberWindows ? Loc.Instance["Editor_RememberWindows"] : null,
            editor.SwitchWithoutAsking ? Loc.Instance["Editor_SwitchWithoutAsking"] : null,
        ]);
    }

    private static string? Join(IEnumerable<string?> parts)
    {
        string text = string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
        return text.Length == 0 ? null : text;
    }

    /// <summary>F2 edits the name.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && ViewModel.HasSelection)
        {
            e.Handled = true;
            FocusName();
        }
    }

    private void OnFocusNameRequested(object? sender, EventArgs e) => FocusName();

    private void FocusName()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnRemoveDisplay(object? sender, RoutedEventArgs e) => _editor?.RemoveDisplayCommand.Execute(_editor.SelectedDisplay);

    private void OnRemoveRule(object? sender, RoutedEventArgs e) => _editor?.Rules.RemoveRuleCommand.Execute(TagOf<RuleCard>(sender));

    /// <summary>"+ Device": the devices not yet in this rule, as a menu under the button.</summary>
    private void OnAddDevice(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button || TagOf<RuleCard>(sender) is not { } card)
        {
            return;
        }

        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (Choice device in card.AvailableDevices)
        {
            var item = new MenuItem { Header = device.Name };
            item.Click += (_, _) => card.AddDeviceCommand.Execute(device);
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0)
        {
            menu.ShowAt(button);
        }
    }

    private static T? TagOf<T>(object? sender)
        where T : class => (sender as Control)?.Tag as T;

    /// <summary>RIGSHIFT_PREVIEW_SELECT=&lt;profile&gt;: screenshots of one profile without clicking.</summary>
    private void PreviewSelect()
    {
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_SELECT") is { Length: > 0 } name
            && ViewModel.Items.FirstOrDefault(i => i.Name == name) is { } item)
        {
            ViewModel.SelectedItem = item;
        }
    }

    /// <summary>RIGSHIFT_PREVIEW_EXPAND=Audio,Apps,Triggers,Behavior opens those groups, once.</summary>
    private void PreviewExpand(ProfileEditorViewModel editor)
    {
        if (_previewDone || Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_EXPAND") is not { Length: > 0 } groups)
        {
            return;
        }

        _previewDone = true;
        foreach (string group in groups.Split(','))
        {
            if (this.FindControl<SettingsCard>(group.Trim() + "Card") is { } card)
            {
                card.IsExpanded = true;
            }
        }

        editor.SelectedDisplayKey ??= editor.Displays.FirstOrDefault(d => d.IsPrimary)?.Key;

        // RIGSHIFT_PREVIEW_SCROLL=Apps scrolls that group to the top once the layout has run.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_SCROLL") is { Length: > 0 } scroll
            && this.FindControl<SettingsCard>(scroll.Trim() + "Card") is { } target)
        {
            DispatcherTimer.RunOnce(() => target.BringIntoView(new global::Avalonia.Rect(0, 0, 1, 2000)), TimeSpan.FromMilliseconds(800));
        }
    }

    private void OnActions(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) =>
        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenPalette();
}
