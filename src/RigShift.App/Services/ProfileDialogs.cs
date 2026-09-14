using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.App.Views;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;
using Wpf.Ui.Controls;

namespace RigShift.App.Services;

/// <summary>Opens the profile editor and the delete confirmation.</summary>
public sealed class ProfileDialogs
{
    private readonly ProfileCatalog _catalog;
    private readonly IDisplayConfigurator _display;
    private readonly IAudioController _audio;
    private readonly SettingsService _settings;
    private readonly HotkeyService _hotkeys;
    private readonly IUsbDeviceList _usbDevices;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    public ProfileDialogs(
        ProfileCatalog catalog,
        IDisplayConfigurator display,
        IAudioController audio,
        SettingsService settings,
        HotkeyService hotkeys,
        IUsbDeviceList usbDevices,
        IServiceProvider services,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _catalog = catalog;
        _display = display;
        _audio = audio;
        _settings = settings;
        _hotkeys = hotkeys;
        _usbDevices = usbDevices;
        _services = services;
        _log = log.ForContext<ProfileDialogs>();
    }

    /// <summary>Editor pre-filled with the active displays and the default playback device.</summary>
    /// <returns>The saved profile, or <c>null</c> if cancelled.</returns>
    public async Task<Profile?> CreateFromCurrentAsync()
    {
        DisplaySnapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => _display.QueryAsync(CancellationToken.None));
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "Current arrangement could not be read for a new profile");
            snapshot = new DisplaySnapshot { TakenAt = DateTimeOffset.Now, Displays = [] };
        }

        IReadOnlyList<AudioDeviceInfo> playback = await ListAudioAsync(AudioDirection.Render);
        string name = ProfileEditing.UniqueName(Loc.Instance["Editor_NewName"], _catalog.Profiles.Select(p => p.Name));
        AudioEndpoint? defaultPlayback = playback.FirstOrDefault(d => d.IsDefault && d.IsActive)?.Endpoint;
        Profile profile = ProfileEditing.Capture(name, snapshot, new AudioAssignment { Playback = defaultPlayback }, _catalog.KnownDisplayNames);

        return await ShowAsync(profile, isNew: true, playback);
    }

    /// <returns>The saved profile, or <c>null</c> if cancelled.</returns>
    public async Task<Profile?> EditAsync(Profile profile) =>
        await ShowAsync(profile, isNew: false, await ListAudioAsync(AudioDirection.Render));

    /// <summary>Delete is destructive: red button, centred on the window it came from (analysis finding I-15).</summary>
    public static async Task<bool> ConfirmDeleteAsync(string name)
    {
        var box = new MessageBox
        {
            Title = Loc.Instance["Profile_DeleteTitle"],
            Content = Loc.Format("Profile_DeleteText", name),
            PrimaryButtonText = Loc.Instance["Profile_Delete"],
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = Loc.Instance["Common_Cancel"],
        };
        SetOwner(box, ActiveWindow());
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    /// <summary>Same style as deleting a profile (analysis finding I-12).</summary>
    /// <param name="deviceName">The rule's USB device, or <c>null</c> when none is chosen.</param>
    public static async Task<bool> ConfirmDeleteRuleAsync(string? deviceName)
    {
        var box = new MessageBox
        {
            Title = Loc.Instance["Automation_DeleteTitle"],
            Content = deviceName is null ? Loc.Instance["Automation_DeleteTextNoDevice"] : Loc.Format("Automation_DeleteText", deviceName),
            PrimaryButtonText = Loc.Instance["Profile_Delete"],
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = Loc.Instance["Common_Cancel"],
        };
        SetOwner(box, ActiveWindow());
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    /// <summary>Asked when the editor closes with unsaved changes (analysis finding I-11). True: discard them.</summary>
    public static async Task<bool> ConfirmDiscardAsync(System.Windows.Window owner)
    {
        var box = new MessageBox
        {
            Title = Loc.Instance["Editor_DiscardTitle"],
            Content = Loc.Instance["Editor_DiscardText"],
            PrimaryButtonText = Loc.Instance["Editor_Discard"],
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = Loc.Instance["Editor_KeepEditing"],
        };
        SetOwner(box, owner);
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    private static System.Windows.Window? ActiveWindow()
    {
        System.Windows.Application? app = System.Windows.Application.Current;
        return app?.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
            ?? (app?.MainWindow is { IsVisible: true } main ? main : null);
    }

    private static void SetOwner(MessageBox box, System.Windows.Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            box.Owner = owner;
            box.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
        }
    }

    private async Task<Profile?> ShowAsync(Profile profile, bool isNew, IReadOnlyList<AudioDeviceInfo> playback)
    {
        IReadOnlyList<AudioDeviceInfo> recording = await ListAudioAsync(AudioDirection.Capture);
        IReadOnlyList<Core.Automation.UsbDevice> usbDevices = await ListUsbDevicesAsync();
        Core.Settings.AppSettings settings = _settings.Current;
        var viewModel = new ProfileEditorViewModel(
            profile, isNew, playback, recording, usbDevices, settings.UsbDeviceNames,
            [.. ViewModels.UsbDeviceChoices.Known(settings.AutomationRules, _catalog.Profiles, settings.UsbDeviceNames)],
            confirmationEnabled: settings.ConfirmTimeoutSeconds > 0, _catalog, _display, _hotkeys, _log);

        MainWindow main = _services.GetRequiredService<MainWindow>();
        var window = new ProfileEditorWindow(viewModel) { Owner = main.IsVisible ? main : null };

        // Recording a hotkey RigShift holds would switch right away, and the "taken" check would see our own.
        _hotkeys.Suspend();
        try
        {
            return window.ShowDialog() == true ? viewModel.Saved : null;
        }
        finally
        {
            _hotkeys.Resume();
            viewModel.Dispose();
        }
    }

    private async Task<IReadOnlyList<Core.Automation.UsbDevice>> ListUsbDevicesAsync()
    {
        try
        {
            return await Task.Run(_usbDevices.ConnectedDevices);
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
            return await Task.Run(() => _audio.ListAsync(direction, CancellationToken.None));
        }
        catch (COMException ex)
        {
            _log.Warning(ex, "{Direction} devices could not be listed for the editor", direction);
            return [];
        }
    }
}
