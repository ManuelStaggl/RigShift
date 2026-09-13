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

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public IReadOnlyList<Profile> Profiles => _profiles;

    public Profile? Find(Guid id) => _profiles.FirstOrDefault(p => p.Id == id);

    public async Task SaveAsync(Profile profile, CancellationToken cancellationToken)
    {
        await _store.SaveAsync(profile, cancellationToken);
        await ReloadAsync(cancellationToken);
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
            _profiles = await _store.LoadAllAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Profiles could not be loaded from {Directory}", ProfileDirectory);
            _profiles = [];
        }

        Rebuild();
        await RefreshActiveAsync(cancellationToken);
    }

    public async Task RefreshActiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            DisplaySnapshot snapshot = await Task.Run(() => _display.QueryAsync(cancellationToken), cancellationToken);
            ActiveProfile = _matcher.FindActive(_profiles, snapshot);
            _log.Information("Active profile: {Profile}", ActiveProfile?.Name ?? "(none)");
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "Could not determine the active profile");
        }

        UpdateFlags();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        Items.Clear();
        foreach (Profile profile in _profiles)
        {
            Items.Add(new ProfileItem(profile));
        }

        IsEmpty = Items.Count == 0;
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
