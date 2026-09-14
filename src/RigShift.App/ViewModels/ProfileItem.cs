using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

/// <summary>A profile as shown in the tray popup, the tray menu and the profile page.</summary>
public sealed partial class ProfileItem(Profile profile) : ObservableObject
{
    public Profile Profile { get; } = profile;

    public string Name => Profile.Name;

    /// <summary>Known symbol key, or <c>null</c> for no symbol.</summary>
    public string? IconKey => ProfileIcons.Normalize(Profile.Icon);

    /// <summary>A profile saved from the command line has no symbol; its card then starts with the name.</summary>
    public bool HasIcon => IconKey is not null;

    /// <summary>Name for screen readers; the active state is also shown as text and check mark, not only by color.</summary>
    public string AccessibleName => IsActive ? $"{Name}, {Loc.Instance["Profile_Active"]}" : Name;

    /// <summary>Left to right, as the displays stand on the desk.</summary>
    public IReadOnlyList<string> DisplayLines { get; } = profile.Displays
        .OrderBy(d => d.PositionX)
        .ThenBy(d => d.PositionY)
        .Select(Describe)
        .ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    [ObservableProperty]
    public partial string? CheckMessage { get; set; }

    private static string Describe(DisplayAssignment display)
    {
        double hertz = RefreshRate.Of(display).Hertz;
        string text = string.Create(Loc.Instance.Culture,
            $"{SwitchMessages.NameOf(display)} · {display.Width} × {display.Height} @ {hertz:0.##} Hz");
        if (display.IsPrimary)
        {
            text += " · " + Loc.Instance["Profile_Primary"];
        }

        if (display.IsOptional)
        {
            text += " · " + Loc.Instance["Profile_Optional"];
        }

        return text;
    }
}
