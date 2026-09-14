using RigShift.Core.Profiles;
using RigShift.Core.Topology;

namespace RigShift.Core.Tests;

/// <summary>
/// Invented but realistic fixtures modelled on the reference setup (desk: 4K@165 + 2×1080p@100; rig: 5120×1440@240
/// + spacedesk tablet). No real device paths or IDs from the owner's machine.
/// </summary>
internal static class TestDisplays
{
    public const string Gpu = @"\\?\PCI#VEN_10DE&DEV_0000#TEST#1";
    public const string Spacedesk = @"\\?\SWD#SPACEDESK#TEST#1";

    public static readonly DisplayIdentity Ultrawide = Identity(Gpu, @"\\?\DISPLAY#SAM0001#TEST&1", 0x4C2D, 0x0001, "Ultrawide 49");
    public static readonly DisplayIdentity Desk4K = Identity(Gpu, @"\\?\DISPLAY#AUS0002#TEST&2", 0x0469, 0x0002, "Desk 4K");
    public static readonly DisplayIdentity DeskLeft = Identity(Gpu, @"\\?\DISPLAY#DEL0003#TEST&3", 0x10AC, 0x0003, "Desk left");
    public static readonly DisplayIdentity DeskRight = Identity(Gpu, @"\\?\DISPLAY#DEL0003#TEST&4", 0x10AC, 0x0003, "Desk right");
    public static readonly DisplayIdentity Tablet = Identity(Spacedesk, @"\\?\DISPLAY#Default_Monitor#TEST&5");

    public static DisplayIdentity Identity(string adapter, string target, ushort manufacturer = 0, ushort product = 0, string name = "") =>
        new()
        {
            AdapterDevicePath = adapter,
            TargetDevicePath = target,
            EdidManufacturerId = manufacturer,
            EdidProductCodeId = product,
            FriendlyName = name,
        };

    public static DisplayAssignment Mode(
        DisplayIdentity identity, int width, int height, uint hertz, bool primary = false, bool optional = false, int x = 0) =>
        new()
        {
            Identity = identity,
            Width = width,
            Height = height,
            RefreshNumerator = hertz * 1000,
            RefreshDenominator = 1000,
            PositionX = x,
            PositionY = 0,
            IsPrimary = primary,
            IsOptional = optional,
        };

    public static DisplayAssignment UltrawideMode => Mode(Ultrawide, 5120, 1440, 240, primary: true);

    public static DisplayAssignment TabletMode => Mode(Tablet, 1920, 1080, 60, optional: true, x: 5120);

    public static IReadOnlyList<DisplayAssignment> DeskModes =>
    [
        Mode(Desk4K, 3840, 2160, 165, primary: true),
        Mode(DeskLeft, 1920, 1080, 100, x: -1920),
        Mode(DeskRight, 1920, 1080, 100, x: 3840),
    ];

    /// <param name="confirm">False (default): the profile switches without asking, so most tests need no confirmation fake.</param>
    public static Profile Profile(string name, IEnumerable<DisplayAssignment> displays, bool confirm = false, AudioAssignment? audio = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Displays = displays.ToList(),
            SwitchWithoutAsking = !confirm,
            Audio = audio ?? new AudioAssignment(),
        };

    public static Profile Rig(bool confirm = false, AudioAssignment? audio = null) =>
        Profile("Rig", [UltrawideMode, TabletMode], confirm, audio);

    public static AttachedDisplay Attached(DisplayIdentity identity, bool available = true, DisplayAssignment? activeMode = null) =>
        new()
        {
            Identity = identity,
            IsAvailable = available,
            IsActive = activeMode is not null,
            ActiveMode = activeMode,
            NativeHandle = identity.TargetDevicePath,
        };

    public static DisplaySnapshot Snapshot(params AttachedDisplay[] displays) =>
        new() { TakenAt = DateTimeOffset.UnixEpoch, Displays = displays };

    /// <summary>Desk active; ultrawide and tablet connected but inactive.</summary>
    public static DisplaySnapshot DeskActive(bool ultrawideAvailable = true, bool tabletAttached = true)
    {
        var displays = new List<AttachedDisplay>
        {
            Attached(Desk4K, activeMode: DeskModes[0]),
            Attached(DeskLeft, activeMode: DeskModes[1]),
            Attached(DeskRight, activeMode: DeskModes[2]),
            Attached(Ultrawide, ultrawideAvailable),
        };
        if (tabletAttached)
        {
            displays.Add(Attached(Tablet));
        }

        return Snapshot([.. displays]);
    }
}
