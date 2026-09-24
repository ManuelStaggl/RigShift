using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

public sealed record AudioChoice(AudioEndpoint? Endpoint, string Name);

/// <summary>One audio role in the editor: "don't change" or a device of this machine.</summary>
public sealed partial class AudioSlot : ObservableObject
{
    private readonly string _labelKey;
    private readonly string _noneKey;
    private readonly IReadOnlyList<AudioDeviceInfo> _devices;
    private readonly AudioEndpoint? _saved;

    /// <param name="labelKey">Text key of the role, e.g. <c>Audio_Playback</c>.</param>
    /// <param name="noneKey">Text key of the "don't change" entry.</param>
    public AudioSlot(
        string labelKey, string noneKey, IReadOnlyList<AudioDeviceInfo> devices, AudioEndpoint? current, int? volume = null, bool supportsVolume = false)
    {
        ArgumentNullException.ThrowIfNull(devices);

        _labelKey = labelKey;
        _noneKey = noneKey;
        _devices = devices;
        _saved = current;
        Label = Loc.Instance[labelKey];
        SupportsVolume = supportsVolume;
        SetVolume = volume is not null;
        Volume = volume ?? 50;
        FillChoices(current);
    }

    [ObservableProperty]
    public partial string Label { get; private set; }

    /// <summary>New texts after a language change, same device.</summary>
    internal void Relabel()
    {
        Label = Loc.Instance[_labelKey];
        FillChoices(Endpoint);
        OnPropertyChanged(nameof(VolumeText));
    }

    private void FillChoices(AudioEndpoint? selected)
    {
        Choices.Clear();
        Choices.Add(new AudioChoice(null, Loc.Instance[_noneKey]));
        foreach (AudioDeviceInfo device in _devices.OrderByDescending(d => d.IsActive).ThenBy(d => d.Endpoint.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
        {
            string name = device.IsActive ? device.Endpoint.FriendlyName : Loc.Format("Audio_NotConnected", device.Endpoint.FriendlyName);
            Choices.Add(new AudioChoice(device.Endpoint, name));
        }

        if (_saved is not null && !_devices.Any(d => SameDevice(d.Endpoint, _saved)))
        {
            // Keep a device this machine does not know (profile copied from another PC) instead of silently dropping it.
            Choices.Add(new AudioChoice(_saved, Loc.Format("Audio_Unknown", _saved.FriendlyName)));
        }

        Selected = selected is null ? Choices[0] : Choices.First(c => c.Endpoint is { } e && SameDevice(e, selected));
    }

    public ObservableCollection<AudioChoice> Choices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevice))]
    public partial AudioChoice? Selected { get; set; }

    public AudioEndpoint? Endpoint => Selected?.Endpoint;

    /// <summary>Only playback and recording get a volume; the call roles usually share their device.</summary>
    public bool SupportsVolume { get; }

    /// <summary>A volume belongs to a device, so it can only be set once one is chosen.</summary>
    public bool HasDevice => Endpoint is not null;

    [ObservableProperty]
    public partial bool SetVolume { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    public partial double Volume { get; set; }

    public string VolumeText => Loc.Format("Audio_VolumeValue", (int)Math.Round(Volume));

    public int? VolumePercent => SupportsVolume && SetVolume && HasDevice ? (int)Math.Clamp(Math.Round(Volume), 0, 100) : null;

    private static bool SameDevice(AudioEndpoint a, AudioEndpoint b) =>
        string.Equals(a.EndpointId, b.EndpointId, StringComparison.OrdinalIgnoreCase);
}
