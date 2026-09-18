using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.Windows.Apps;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// The app picker (finding HW-11): installed and running programs with icon and search; a file dialog stays available for
/// programs without a Start menu entry.
/// </summary>
public sealed partial class AppPickerViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<DiscoveredApp>> _find;
    private readonly Func<string, ImageSource?> _loadIcon;
    private readonly ILogger _log;
    private List<AppChoice> _all = [];

    public AppPickerViewModel(Func<IReadOnlyList<DiscoveredApp>> find, Func<string, ImageSource?> loadIcon, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _find = find;
        _loadIcon = loadIcon;
        _log = log.ForContext<AppPickerViewModel>();
        Query = string.Empty;
        IsLoading = true;
    }

    public ObservableCollection<AppChoice> Apps { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChoose))]
    public partial AppChoice? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; }

    public bool IsEmpty => !IsLoading && Apps.Count == 0;

    public bool CanChoose => Selected is not null;

    public async Task LoadAsync()
    {
        try
        {
            IReadOnlyList<DiscoveredApp> found = await Task.Run(_find);
            _all = [.. found.Select(a => new AppChoice(a))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or COMException)
        {
            _log.Warning(ex, "Apps could not be listed for the picker");
        }

        IsLoading = false;
        Filter();

        List<AppChoice> all = _all;
        List<ImageSource?> icons = await Task.Run(() => all.Select(a => _loadIcon(a.Path)).ToList());
        for (int i = 0; i < all.Count; i++)
        {
            all[i].Icon = icons[i];
        }
    }

    partial void OnQueryChanged(string value) => Filter();

    /// <summary>With a search, the best match is selected, so Enter takes it.</summary>
    private void Filter()
    {
        AppChoice? selected = Selected;
        Apps.Clear();
        foreach (AppChoice app in _all.Where(a => AppDiscovery.Matches(a.App, Query)))
        {
            Apps.Add(app);
        }

        Selected = selected is not null && Apps.Contains(selected) ? selected
            : !string.IsNullOrWhiteSpace(Query) && Apps.Count > 0 ? Apps[0]
            : null;
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>One program in the picker; the icon arrives after the list.</summary>
public sealed partial class AppChoice(DiscoveredApp app) : ObservableObject
{
    public DiscoveredApp App { get; } = app;

    public string Name => App.Name;

    public string Path => App.Path;

    /// <summary>The path as Windows writes it: Start menu shortcuts sometimes come with forward slashes.</summary>
    public string ShownPath => Path.Replace('/', '\\');

    public bool IsRunning => App.IsRunning;

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    /// <summary>Screen readers read the list item's text.</summary>
    public override string ToString() => Name;
}
