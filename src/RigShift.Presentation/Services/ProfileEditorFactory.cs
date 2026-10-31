using System.ComponentModel;
using System.Runtime.InteropServices;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// What the profile pages build without asking anything: a new profile from the active displays, and the editor for a
/// profile with its audio and USB lists, Surround state and rules. Used by the dialogs of the app.
/// </summary>
public sealed class ProfileEditorFactory(
    ProfileEditorServices editorServices,
    IAudioController audio,
    IUsbDeviceList usbDevices,
    ISurroundController surround,
    IUsbPowerCheck powerCheck)
{
    private readonly ILogger _log = editorServices.Log.ForContext<ProfileEditorFactory>();

    /// <summary>A new, unsaved profile from the active displays, the default playback and recording devices and Surround.</summary>
    public async Task<Profile> NewFromCurrentAsync()
    {
        DisplaySnapshot snapshot;
        try
        {
            snapshot = await editorServices.Display.QueryAsync(CancellationToken.None);
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "Current arrangement could not be read for a new profile");
            snapshot = new DisplaySnapshot { TakenAt = DateTimeOffset.Now, Displays = [] };
        }

        ProfileCatalog catalog = editorServices.Catalog;
        IReadOnlyList<AudioDeviceInfo> playback = await ListAudioAsync(AudioDirection.Render);
        IReadOnlyList<AudioDeviceInfo> recording = await ListAudioAsync(AudioDirection.Capture);
        string name = ProfileEditing.UniqueName(Loc.Instance["Editor_NewName"], catalog.Profiles.Select(p => p.Name));
        var audio = new AudioAssignment
        {
            Playback = playback.FirstOrDefault(d => d.IsDefault && d.IsActive)?.Endpoint,
            Recording = recording.FirstOrDefault(d => d.IsDefault && d.IsActive)?.Endpoint,
        };
        Profile profile = ProfileEditing.Capture(name, snapshot, audio, catalog.KnownDisplayNames);

        // A running Surround grid is part of "the arrangement as it is now" - without it the profile could never
        // reproduce the wide display it just recorded.
        SurroundState state = await ReadSurroundAsync();
        if (state.Grids.Count > 0)
        {
            profile = profile with { Surround = new SurroundSetting { Enabled = true, Grid = state.Grids[0] } };
        }

        return profile;
    }

    /// <summary>The detail's editor for a profile: audio and USB lists, Surround and the profile's rules from the settings.</summary>
    public async Task<ProfileEditorViewModel> CreateEditorAsync(Profile profile, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(profile);
        IReadOnlyList<AudioDeviceInfo> playback = await ListAudioAsync(AudioDirection.Render);
        IReadOnlyList<AudioDeviceInfo> recording = await ListAudioAsync(AudioDirection.Capture);
        IReadOnlyList<Core.Automation.UsbDevice> devices = await ListUsbDevicesAsync();
        SurroundState state = await ReadSurroundAsync();
        Core.Settings.AppSettings settings = editorServices.Settings.Current;
        ProfileCatalog catalog = editorServices.Catalog;
        var rules = new ProfileRulesEditor(
            profile.Id, settings.AutomationRules ?? [], catalog.Profiles, settings.DefaultProfileId, devices, settings.UsbDeviceNames,
            powerCheck, _log, new UsbDetection(usbDevices, TimeProvider.System));
        var appsWaitDevice = new AppsWaitDeviceChoice(
            profile.AppsWaitForUsbDeviceId, profile.AppsWaitForUsbDeviceName, devices,
            UsbDeviceChoices.Known(settings.AutomationRules, catalog.Profiles, settings.UsbDeviceNames), settings.UsbDeviceNames,
            new UsbDetection(usbDevices, TimeProvider.System));
        var context = new ProfileEditorContext(
            playback, recording, appsWaitDevice, state, rules, ConfirmationEnabled: settings.ConfirmTimeoutSeconds > 0,
            SurroundUsedBy: SurroundDefaults.UsedByAnother(profile.Id, catalog.Profiles)?.Name);
        return new ProfileEditorViewModel(profile, isNew, context, editorServices);
    }

    /// <summary>The Surround state, or "no driver" when it cannot be read - the editor then simply hides the section.</summary>
    private async Task<SurroundState> ReadSurroundAsync()
    {
        try
        {
            return await surround.QueryAsync(CancellationToken.None);
        }
        catch (Exception ex) when (DisplayApiFailure.Is(ex))
        {
            _log.Warning(ex, "Surround state could not be read for the editor");
            return SurroundState.Unavailable(SurroundAvailability.Unknown, ex.Message);
        }
    }

    private async Task<IReadOnlyList<Core.Automation.UsbDevice>> ListUsbDevicesAsync()
    {
        try
        {
            return await Task.Run(usbDevices.ConnectedDevices);
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "USB devices could not be listed for the editor");
            return [];
        }
    }

    private async Task<IReadOnlyList<AudioDeviceInfo>> ListAudioAsync(AudioDirection direction)
    {
        try
        {
            return await audio.ListAsync(direction, CancellationToken.None);
        }
        catch (COMException ex)
        {
            _log.Warning(ex, "{Direction} devices could not be listed for the editor", direction);
            return [];
        }
    }
}
