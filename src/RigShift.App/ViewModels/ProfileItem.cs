using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

/// <summary>A profile as shown in the tray popup, the tray menu and the profile page.</summary>
/// <param name="usbDeviceNames">Custom USB device names, for the device the apps wait for.</param>
public sealed partial class ProfileItem(Profile profile, IReadOnlyDictionary<string, string>? usbDeviceNames = null) : ObservableObject
{
    private IReadOnlyList<ImageSource>? _appIcons;

    public Profile Profile { get; } = profile;

    public string Name => Profile.Name;

    /// <summary>Known symbol key, or <c>null</c> for no symbol.</summary>
    public string? IconKey => ProfileIcons.Normalize(Profile.Icon);

    /// <summary>A profile saved from the command line has no symbol; its card then starts with the name.</summary>
    public bool HasIcon => IconKey is not null;

    /// <summary>Name for screen readers; the active state is also shown as text and check mark, not only by color.</summary>
    public string AccessibleName => IsActive ? $"{Name}, {Loc.Instance["Profile_Active"]}" : Name;

    /// <summary>"Default · Active" below the name on the profile card; <c>null</c> when neither applies.</summary>
    public string? StatusText => (IsDefault, IsActive) switch
    {
        (true, true) => $"{Loc.Instance["Profile_Default"]} · {Loc.Instance["Profile_Active"]}",
        (true, false) => Loc.Instance["Profile_Default"],
        (false, true) => Loc.Instance["Profile_Active"],
        _ => null,
    };

    /// <summary>Left to right, as the displays stand on the desk.</summary>
    public IReadOnlyList<string> DisplayLines { get; } = profile.Displays
        .OrderBy(d => d.PositionX)
        .ThenBy(d => d.PositionY)
        .Select(Describe)
        .ToList();

    /// <summary>"SimHub, CrewChief · waits for Simagic Base"; <c>null</c> without apps (finding HW-09).</summary>
    public string? AppsLine { get; } = DescribeApps(profile, usbDeviceNames);

    public bool HasApps => AppsLine is not null;

    /// <summary>The programs' own icons, read when the card first shows them; programs without one are left out.</summary>
    public IReadOnlyList<ImageSource> AppIcons => _appIcons ??= Profile.Apps
        .Select(app => Services.AppIcons.Load(app.Path))
        .OfType<ImageSource>()
        .ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(StatusText))]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
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

    private static string? DescribeApps(Profile profile, IReadOnlyDictionary<string, string>? usbDeviceNames)
    {
        if (profile.Apps.Count == 0)
        {
            return null;
        }

        string text = string.Join(", ", profile.Apps.Select(app =>
            app.Kind == AppActionKind.Stop ? Loc.Format("Profile_AppStop", AppName(app)) : AppName(app)));
        if (profile.AppsWaitForUsbDeviceId is not null)
        {
            text += " · " + Loc.Format("Profile_AppsWait",
                UsbDeviceNames.NameOf(profile.AppsWaitForUsbDeviceId, profile.AppsWaitForUsbDeviceName, usbDeviceNames));
        }

        return text;
    }

    private static string AppName(AppAction app) =>
        app.Name ?? (Path.GetFileNameWithoutExtension(app.Path) is { Length: > 0 } name ? name : app.Path);
}
