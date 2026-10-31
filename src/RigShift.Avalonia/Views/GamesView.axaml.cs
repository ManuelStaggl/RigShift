using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using Ursa.Controls;
using Path = Avalonia.Controls.Shapes.Path;

namespace RigShift.Avalonia.Views;

/// <summary>
/// The games page as one sheet per game, on <see cref="GamesViewModel"/> like the profiles sheet: what starts and the
/// profile it switches to on top, the rest of a session in groups in the order it runs. The view adds the symbol
/// menu, the game's icon in the title and the one-line summaries of the closed groups.
/// </summary>
public sealed partial class GamesView : UserControl
{
    /// <summary>The model's "Profile" tab: a new game opens on its first decision.</summary>
    private const int ProfileTab = 1;

    private GameEditorViewModel? _editor;
    private GameItem? _item;
    private bool _previewDone;

    public GamesView()
    {
        ViewModel = App.Provider.GetRequiredService<GamesViewModel>();
        DataContext = ViewModel;
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            ViewModel.PropertyChanged += OnViewModelChanged;
            ViewModel.FocusNameRequested += OnFocusNameRequested;
            PreviewSelect();
            ViewModel.PageShown();
            FollowItem(ViewModel.SelectedItem);
            Attach(ViewModel.Editor);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            ViewModel.FocusNameRequested -= OnFocusNameRequested;
            FollowItem(null);
            Attach(null);
        };
        KeyDown += OnKeyDown;
    }

    public GamesViewModel ViewModel { get; }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GamesViewModel.Editor):
                Attach(ViewModel.Editor);
                break;
            case nameof(GamesViewModel.SelectedTabIndex) when ViewModel.SelectedTabIndex == ProfileTab:
                // Once the new editor's layout has run.
                Dispatcher.UIThread.Post(() => ProfileWell.BringIntoView(), DispatcherPriority.Loaded);
                break;
            case nameof(GamesViewModel.SelectedItem):
                FollowItem(ViewModel.SelectedItem);
                break;
        }
    }

    /// <summary>A new editor behind the sheet: its symbol menu and summaries follow it.</summary>
    private void Attach(GameEditorViewModel? editor)
    {
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorChanged;
            _editor.AppList.Items.CollectionChanged -= OnAppsChanged;
        }

        _editor = editor;
        if (editor is null)
        {
            return;
        }

        editor.PropertyChanged += OnEditorChanged;
        editor.AppList.Items.CollectionChanged += OnAppsChanged;
        var iconMenu = (MenuFlyout)IconButton.Flyout!;
        iconMenu.Items.Clear();
        foreach (Choice icon in editor.IconChoices)
        {
            var item = new MenuItem
            {
                Header = icon.Name,
                Icon = new Path { Data = icon.Key is null ? Icons.Games20 : ProfileSymbols.For(icon.Key, 20), Classes = { "icon", "md" } },
            };
            item.Click += (_, _) => editor.ChooseIconCommand.Execute(icon);
            iconMenu.Items.Add(item);
        }

        ShowIcon();
        ShowSummaries();
        PreviewExpand();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameEditorViewModel.SelectedIcon))
        {
            ShowIcon();
        }

        ShowSummaries();
    }

    private void OnAppsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ShowSummaries();

    /// <summary>The selected game's own icon arrives after the list is up; the title follows it.</summary>
    private void FollowItem(GameItem? item)
    {
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemChanged;
        }

        _item = item;
        if (item is not null)
        {
            item.PropertyChanged += OnItemChanged;
        }

        ShowIcon();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameItem.GameIcon))
        {
            ShowIcon();
        }
    }

    /// <summary>A picked symbol wins; without one the game's own icon, else the generic game glyph.</summary>
    private void ShowIcon()
    {
        string? key = _editor?.SelectedIcon?.Key;
        byte[]? own = key is null ? ViewModel.SelectedItem?.GameIcon as byte[] : null;
        IconImage.Source = own is null ? null : new Bitmap(new MemoryStream(own));
        IconImage.IsVisible = own is not null;
        IconPath.IsVisible = own is null;
        IconPath.Data = key is null ? Icons.Games24 : ProfileSymbols.For(key, 24);
    }

    /// <summary>What each closed group holds, in one line: the sheet reads like the session without opening anything.</summary>
    private void ShowSummaries()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        LaunchCard.Description = Join([
            editor.SupportsArguments ? editor.Arguments : null,
            editor.ProcessName.Length > 0 ? editor.ProcessName + ".exe" : Loc.Instance["Game_ProcessLearned"],
            editor.SelectedEnd?.Name,
            editor.StartWithGame ? Loc.Instance["Game_StartWithGame"] : null,
        ]);
        GameSettingsCard.Description = editor.SettingsEnabled ? editor.SettingsStatus : null;
        ToolsCard.Description = Join(editor.AppList.Items.Select(a => a.DisplayName));
        WindowsCard.Description = editor.WindowsText;
        EndCard.Description = Join([
            editor.SelectedExit?.Name,
            editor.StopApps ? Loc.Instance["Game_StopApps"] : null,
        ]);
        TriggersCard.Description = Join([editor.HotkeyText]);
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

    /// <summary>"Not recognised" leads to the process name in the game group.</summary>
    private void OnShowGameGroup(object? sender, RoutedEventArgs e)
    {
        LaunchCard.IsExpanded = true;
        Dispatcher.UIThread.Post(() => LaunchCard.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void OnActions(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OpenPalette();

    /// <summary>RIGSHIFT_PREVIEW_SELECT=&lt;game&gt;: screenshots of one game without clicking.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void PreviewSelect()
    {
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_SELECT") is { Length: > 0 } name
            && ViewModel.Items.FirstOrDefault(i => i.Name == name) is { } item)
        {
            ViewModel.SelectedItem = item;
        }
    }

    /// <summary>RIGSHIFT_PREVIEW_EXPAND=Launch,GameSettings,Tools,Windows,End,Triggers opens those groups, once.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void PreviewExpand()
    {
        if (_previewDone || Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_EXPAND") is not { Length: > 0 } groups)
        {
            return;
        }

        _previewDone = true;
        foreach (string group in groups.Split(','))
        {
            if (this.FindControl<Control>(group.Trim() + "Card") is SettingsCard card)
            {
                card.IsExpanded = true;
            }
        }

        // RIGSHIFT_PREVIEW_SCROLL=Launch scrolls that group to the top once the layout has run.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_SCROLL") is { Length: > 0 } scroll
            && this.FindControl<Control>(scroll.Trim() + "Card") is SettingsCard target)
        {
            DispatcherTimer.RunOnce(() => target.BringIntoView(new global::Avalonia.Rect(0, 0, 1, 2000)), TimeSpan.FromMilliseconds(800));
        }
    }
}
