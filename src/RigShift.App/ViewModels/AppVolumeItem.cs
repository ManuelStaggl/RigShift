using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

/// <summary>One program's volume in the profile editor.</summary>
public sealed partial class AppVolumeItem : ObservableObject
{
    public AppVolumeItem(AppVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        Executable = volume.Executable;
        Volume = volume.VolumePercent;
        Muted = volume.Muted;
    }

    public string Executable { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    public partial double Volume { get; set; }

    [ObservableProperty]
    public partial bool Muted { get; set; }

    public string VolumeText => Loc.Format("Audio_VolumeValue", (int)Math.Round(Volume));

    internal void Relabel() => OnPropertyChanged(nameof(VolumeText));

    public AppVolume Build() => new() { Executable = Executable, VolumePercent = (int)Math.Round(Volume), Muted = Muted };
}
