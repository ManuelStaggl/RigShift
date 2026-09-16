using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RigShift.Windows.Display;

/// <summary>
/// NVAPI structures, laid out exactly as in NVIDIA's nvapi.h. Every field is four bytes wide, so the sequential
/// layout needs no padding; the sizes are pinned down by tests, because a wrong size means a wrong version word and
/// the driver writing past the end of a buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MosaicDisplaySettingV1
{
    internal uint Version;
    internal uint Width;
    internal uint Height;
    internal uint BitsPerPixel;
    internal uint Frequency;

    internal static uint Version1 => NvApi.MakeVersion<MosaicDisplaySettingV1>(1);
}

/// <summary>NV_MOSAIC_GRID_TOPO_DISPLAY_V2. The version 1 form has neither the version nor the pixel shift field.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MosaicGridTopoDisplayV2
{
    internal uint Version;
    internal uint DisplayId;

    /// <summary>Positive overlaps the neighbour, negative leaves a gap (bezel).</summary>
    internal int OverlapX;
    internal int OverlapY;

    /// <summary>NV_ROTATE: 0 = none, 1 = 90 degrees, 2 = 180, 3 = 270.</summary>
    internal uint Rotation;

    /// <summary>Reserved by NVIDIA, must stay zero.</summary>
    internal uint CloneGroup;

    internal uint PixelShiftType;

    internal static uint Version2 => NvApi.MakeVersion<MosaicGridTopoDisplayV2>(2);
}

/// <summary>The 64 display slots of a grid, in cell order.</summary>
[InlineArray(NvApi.MaxMosaicDisplays)]
internal struct MosaicGridTopoDisplays
{
    private MosaicGridTopoDisplayV2 _element0;
}

/// <summary>
/// NV_MOSAIC_GRID_TOPO_V2. The five one-bit flags plus 26 reserved bits of the header are one 32-bit word here:
/// C# has no bit fields, and the only flag this code ever sets is the lowest one.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MosaicGridTopoV2
{
    internal uint Version;
    internal uint Rows;
    internal uint Columns;
    internal uint DisplayCount;
    internal uint Flags;
    internal MosaicGridTopoDisplays Displays;
    internal MosaicDisplaySettingV1 DisplaySettings;

    internal static uint StructVersion => NvApi.MakeVersion<MosaicGridTopoV2>(2);

    /// <summary>applyWithBezelCorrect, bit 0: switch to the bezel-corrected resolution when enabling.</summary>
    internal const uint FlagApplyWithBezelCorrect = 1u << 0;

    /// <summary>driverReloadAllowed, bit 3. Never set - see the flags on the set call.</summary>
    internal const uint FlagDriverReloadAllowed = 1u << 3;
}

/// <summary>One display's verdict inside <see cref="MosaicDisplayTopoStatus"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MosaicDisplayTopoStatusDisplay
{
    internal uint DisplayId;

    /// <summary>NV_MOSAIC_DISPLAYCAPS_PROBLEM_* flags; see <see cref="MosaicProblems.Describe"/>.</summary>
    internal uint ErrorFlags;
    internal uint WarningFlags;

    /// <summary>supportsRotation in bit 0, the rest reserved.</summary>
    internal uint Capabilities;
}

/// <summary>The 128 display slots of a validation result.</summary>
[InlineArray(NvApi.MaxDisplays)]
internal struct MosaicDisplayTopoStatusDisplays
{
    private MosaicDisplayTopoStatusDisplay _element0;
}

/// <summary>NV_MOSAIC_DISPLAY_TOPO_STATUS: what is wrong with a grid, filled in by the validate call.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MosaicDisplayTopoStatus
{
    internal uint Version;
    internal uint ErrorFlags;
    internal uint WarningFlags;
    internal uint DisplayCount;
    internal MosaicDisplayTopoStatusDisplays Displays;

    internal static uint StructVersion => NvApi.MakeVersion<MosaicDisplayTopoStatus>(1);

    /// <summary>NV_MOSAIC_DISPLAYTOPO_WARNING_DRIVER_RELOAD_REQUIRED.</summary>
    internal const uint WarningDriverReloadRequired = 1u << 1;
}

/// <summary>NV_DISPLAY_ID_INFO_DATA_V1: which Windows adapter and target a driver display id sits on.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DisplayIdInfo
{
    internal uint Version;
    internal uint AdapterLuidLow;
    internal int AdapterLuidHigh;
    internal uint TargetId;
    private readonly uint _reserved0;
    private readonly uint _reserved1;
    private readonly uint _reserved2;
    private readonly uint _reserved3;

    internal static uint StructVersion => NvApi.MakeVersion<DisplayIdInfo>(1);
}

/// <summary>Turns the driver's problem flags into words, so a refusal says what is wrong instead of only that it is.</summary>
internal static class MosaicProblems
{
    private static readonly (uint Flag, string Text)[] Problems =
    [
        (1u << 0, "a display sits on a graphics card that cannot be part of the grid"),
        (1u << 1, "a display sits on the wrong connector"),
        (1u << 2, "the displays have no resolution and refresh rate in common"),
        (1u << 3, "a display reports no EDID"),
        (1u << 4, "the displays use different output types"),
        (1u << 5, "a display of the grid is not connected"),
        (1u << 6, "the graphics cards cannot be arranged for this grid"),
        (1u << 7, "the driver does not support this grid"),
        (1u << 8, "the SLI bridge is missing"),
        (1u << 9, "ECC memory is switched on"),
        (1u << 10, "this arrangement of graphics cards is not supported"),
    ];

    /// <summary>The set flags as a sentence, or null when none is set.</summary>
    internal static string? Describe(uint flags)
    {
        if (flags == 0)
        {
            return null;
        }

        IEnumerable<string> found = Problems.Where(p => (flags & p.Flag) != 0).Select(p => p.Text);
        string text = string.Join("; ", found);
        return text.Length > 0 ? text : null;
    }
}
