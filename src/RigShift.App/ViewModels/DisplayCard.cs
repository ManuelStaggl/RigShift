using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;

namespace RigShift.App.ViewModels;

/// <summary>One attached monitor on the displays page. The name is saved when the field loses focus or on Enter.</summary>
public sealed partial class DisplayCard : ObservableObject
{
    private readonly DisplaysViewModel _owner;
    private readonly DisplayIdentity _identity;
    private string? _savedName;

    public DisplayCard(DisplaysViewModel owner, AttachedDisplay display, int? number, string? customName, string profilesText)
    {
        ArgumentNullException.ThrowIfNull(display);

        _owner = owner;
        _identity = display.Identity;
        _savedName = DisplayNames.Normalize(customName);
        CustomName = _savedName ?? string.Empty;
        Number = number;
        Mode = display.ActiveMode;
        ProfilesText = profilesText;

        string state = display.IsActive
            ? Loc.Instance[display.ActiveMode?.IsPrimary == true ? "Displays_StatePrimary" : "Displays_StateActive"]
            : Loc.Instance[display.IsAvailable ? "Displays_StateOff" : "Displays_StateNotReady"];
        DetailsText = Mode is { } mode
            ? state + " · " + Loc.Format("Displays_Mode", mode.Width, mode.Height,
                RefreshRate.Of(mode).Hertz.ToString("0.##", Loc.Instance.Culture))
            : state;
    }

    public string TargetDevicePath => _identity.TargetDevicePath;

    /// <summary>Number of an active display as Windows counts it (<see cref="DisplayNumbers"/>), shown by "Identify".</summary>
    public int? Number { get; }

    public string NumberText => Number?.ToString(Loc.Instance.Culture) ?? "–";

    public DisplayAssignment? Mode { get; }

    public string ModelName => SwitchMessages.NameOf(null, _identity);

    public string Name => SwitchMessages.NameOf(DisplayNames.Normalize(CustomName), _identity);

    public string DetailsText { get; }

    public string ProfilesText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    public partial string CustomName { get; set; }

    internal async Task SaveNameAsync()
    {
        string? name = DisplayNames.Normalize(CustomName);
        if (name == _savedName)
        {
            return;
        }

        _savedName = name;
        await _owner.RenameAsync(this, name);
    }
}
