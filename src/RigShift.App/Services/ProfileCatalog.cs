using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// The loaded profiles and which one is active right now. Lives on the UI thread; OS queries run on the thread pool.
/// </summary>
public sealed partial class ProfileCatalog : ObservableObject
{
    private readonly IProfileStore _store;
    private readonly IDisplayConfigurator _display;
    private readonly ActiveProfileMatcher _matcher;
    private readonly SettingsService _settings;
    private readonly ILogger _log;
    private IReadOnlyList<Profile> _profiles = [];
    private IReadOnlyList<UnreadableProfileFile> _unreadable = [];

    public ProfileCatalog(
        IProfileStore store, IDisplayConfigurator display, ActiveProfileMatcher matcher, SettingsService settings, AppPaths paths, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _display = display;
        _matcher = matcher;
        _settings = settings;
        _log = log.ForContext<ProfileCatalog>();
        ProfileDirectory = paths.Profiles;
        IsEmpty = true;

        settings.Changed += (_, _) => UpdateFlags();
        Loc.Instance.PropertyChanged += (_, _) => Rebuild();
    }

    /// <summary>Raised after profiles or the active profile changed.</summary>
    public event EventHandler? Changed;

    public string ProfileDirectory { get; }

    public ObservableCollection<ProfileItem> Items { get; } = [];

    [ObservableProperty]
    public partial Profile? ActiveProfile { get; set; }

    /// <summary>
    /// The profile that was active before <see cref="ActiveProfile"/> changed – the target of "back to the previous
    /// profile" (1.7.0). Kept while no profile is active, not persisted across restarts.
    /// </summary>
    public Guid? PreviousProfileId { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary><c>true</c> if the last load skipped profile files (locked, broken or from a newer version).</summary>
    [ObservableProperty]
    public partial bool HasUnreadableFiles { get; set; }

    [ObservableProperty]
    public partial string? UnreadableFilesMessage { get; set; }

    /// <summary>Empty-state text; rebuilt with the items, so it follows a language change (analysis finding I-13).</summary>
    [ObservableProperty]
    public partial string? EmptyMessage { get; set; }

    public IReadOnlyList<Profile> Profiles => _profiles;

    public Profile? Find(Guid id) => _profiles.FirstOrDefault(p => p.Id == id);

    /// <summary>Custom monitor names from the settings registry and the profiles.</summary>
    public IReadOnlyDictionary<string, string> KnownDisplayNames => DisplayNames.Known(_profiles, _settings.Current.DisplayNames);

    /// <summary>Rates the display offered at this resolution when it was last active (finding HW-13).</summary>
    public IReadOnlyList<RefreshRate> RememberedRefreshRates(DisplayIdentity identity, int width, int height) =>
        RefreshRateMemory.Get(_settings.Current.RefreshRates, identity, width, height);

    /// <summary>Remembers the rates of every active display, e.g. after a switch; failures are only logged.</summary>
    public async Task RememberActiveRefreshRatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            List<(DisplayIdentity, int, int, IReadOnlyList<RefreshRate>)> found = await Task.Run(async () =>
            {
                DisplaySnapshot snapshot = await _display.QueryAsync(cancellationToken);
                var rates = new List<(DisplayIdentity, int, int, IReadOnlyList<RefreshRate>)>();
                foreach (AttachedDisplay display in snapshot.Displays)
                {
                    if (display.ActiveMode is { } mode)
                    {
                        rates.Add((display.Identity, mode.Width, mode.Height,
                            await _display.ListRefreshRatesAsync(display.Identity, mode.Width, mode.Height, cancellationToken)));
                    }
                }

                return rates;
            }, cancellationToken);
            await RememberRefreshRatesAsync(found, cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or System.Runtime.InteropServices.COMException)
        {
            _log.Warning(ex, "Refresh rates of the active displays could not be read");
        }
    }

    /// <summary>Saves rates that are new or changed; empty lists are ignored. Failures are only logged.</summary>
    public async Task RememberRefreshRatesAsync(
        IReadOnlyList<(DisplayIdentity Identity, int Width, int Height, IReadOnlyList<RefreshRate> Rates)> found, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(found);
        try
        {
            await _settings.UpdateAsync(s =>
            {
                IReadOnlyDictionary<string, IReadOnlyList<string>>? memory = s.RefreshRates;
                foreach ((DisplayIdentity identity, int width, int height, IReadOnlyList<RefreshRate> rates) in found)
                {
                    memory = RefreshRateMemory.With(memory, identity, width, height, rates);
                }

                return ReferenceEquals(memory, s.RefreshRates) ? s : s with { RefreshRates = memory };
            }, cancellationToken, notify: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Refresh rates could not be remembered");
        }
    }

    /// <summary>Names a monitor everywhere: in the settings registry and in every profile that contains it.</summary>
    public async Task RenameDisplayAsync(string targetDevicePath, string? name, CancellationToken cancellationToken)
    {
        await _settings.UpdateAsync(s => s with { DisplayNames = DisplayNames.WithName(s.DisplayNames, targetDevicePath, name) }, cancellationToken);
        IReadOnlyList<Profile> changed = DisplayNames.Rename(_profiles, targetDevicePath, name);
        foreach (Profile profile in changed)
        {
            await _store.SaveAsync(profile, cancellationToken);
        }

        _log.Information("Display {Display} named {Name}; {Count} profile(s) updated", targetDevicePath, DisplayNames.Normalize(name) ?? "(none)", changed.Count);
        if (changed.Count > 0)
        {
            await ReloadAsync(cancellationToken);
        }
    }

    /// <summary>Saves the profile and carries its display names over to every other profile with the same monitor.</summary>
    public async Task SaveAsync(Profile profile, CancellationToken cancellationToken)
    {
        await _store.SaveAsync(profile, cancellationToken);
        foreach (Profile other in DisplayNames.Propagate(profile, _profiles))
        {
            await _store.SaveAsync(other, cancellationToken);
            _log.Information("Display names of profile {Profile} updated from {Source}", other.Name, profile.Name);
        }

        IReadOnlyDictionary<string, string>? registry = _settings.Current.DisplayNames;
        IReadOnlyDictionary<string, string>? updated = registry;
        foreach (DisplayAssignment display in profile.Displays)
        {
            updated = DisplayNames.WithName(updated, display.Identity.TargetDevicePath, display.CustomName);
        }

        if (!SameNames(registry, updated))
        {
            await _settings.UpdateAsync(s => s with { DisplayNames = updated }, cancellationToken);
        }

        await ReloadAsync(cancellationToken);
    }

    /// <summary>Makes the profile the default profile, or leaves no default if it already is.</summary>
    public async Task ToggleDefaultAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Guid? id = _settings.Current.DefaultProfileId == profile.Id ? null : profile.Id;
        await _settings.UpdateAsync(s => s with { DefaultProfileId = id }, cancellationToken);
        _log.Information("Default profile set to {Profile}", id is null ? "(none)" : profile.Name);
    }

    /// <summary>Deletes the profile; if it was the default profile, there is no default afterwards.</summary>
    public async Task DeleteAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await _store.DeleteAsync(profile.Id, cancellationToken);
        if (_settings.Current.DefaultProfileId == profile.Id)
        {
            await _settings.UpdateAsync(s => s with { DefaultProfileId = null }, cancellationToken);
        }

        await ReloadAsync(cancellationToken);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            LoadResult result = await _store.LoadAllAsync(cancellationToken);
            _profiles = result.Profiles;
            _unreadable = result.Unreadable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Profiles could not be loaded from {Directory}", ProfileDirectory);
            _profiles = [];
            _unreadable = [];
        }

        Rebuild();
        await RefreshActiveAsync(cancellationToken);
    }

    public async Task RefreshActiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            DisplaySnapshot snapshot = await Task.Run(() => _display.QueryAsync(cancellationToken), cancellationToken);
            Profile? active = _matcher.FindActive(_profiles, snapshot);
            if (ActiveProfile is { } before && before.Id != active?.Id)
            {
                PreviousProfileId = before.Id;
            }

            ActiveProfile = active;
            _log.Information("Active profile: {Profile}", ActiveProfile?.Name ?? "(none)");
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "Could not determine the active profile");
        }

        UpdateFlags();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameNames(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b) =>
        (a?.Count ?? 0) == (b?.Count ?? 0)
        && (a ?? new Dictionary<string, string>()).All(pair => b is not null && b.TryGetValue(pair.Key, out string? value) && value == pair.Value);

    private void Rebuild()
    {
        Items.Clear();
        foreach (Profile profile in _profiles)
        {
            Items.Add(new ProfileItem(profile, _settings.Current.UsbDeviceNames));
        }

        IsEmpty = Items.Count == 0;
        EmptyMessage = Loc.Format("Profiles_EmptyText", ProfileDirectory);
        HasUnreadableFiles = _unreadable.Count > 0;
        UnreadableFilesMessage = HasUnreadableFiles
            ? Loc.Format("Profiles_UnreadableFiles", _unreadable.Count, string.Join(", ", _unreadable.Select(f => f.FileName)))
            : null;
        UpdateFlags();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateFlags()
    {
        foreach (ProfileItem item in Items)
        {
            item.IsActive = item.Profile.Id == ActiveProfile?.Id;
            item.IsDefault = item.Profile.Id == _settings.Current.DefaultProfileId;
        }
    }
}
