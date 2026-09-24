using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RigShift.Windows.Display;

/// <summary>
/// The slice of NVIDIA's NVAPI that Surround needs. Deliberately hand-written instead of taking a library: the only
/// complete one is under the LGPL, and this repository promises MIT without strings attached. CsWin32 cannot help
/// either - NVAPI is not a Windows API.
///
/// NVAPI exports exactly one symbol, <c>nvapi_QueryInterface</c>; every function is fetched from it by a numeric id.
/// The ids and the structures below come from NVIDIA's published headers (nvapi.h, nvapi_interface.h).
/// </summary>
internal sealed unsafe class NvApi : IDisposable
{
    /// <summary>NV_MOSAIC_MAX_DISPLAYS.</summary>
    internal const int MaxMosaicDisplays = 64;

    /// <summary>NVAPI_MAX_DISPLAYS: NVAPI_PHYSICAL_GPUS (32) * NVAPI_ADVANCED_DISPLAY_HEADS (4).</summary>
    internal const int MaxDisplays = 128;

    /// <summary>NVAPI_SHORT_STRING_MAX.</summary>
    private const int ShortStringMax = 64;

    /// <summary>
    /// Keep the GPU arrangement, and never let the driver reload itself: a forced reload takes down every running
    /// GPU application, which on this machine means the game the user is about to play.
    /// </summary>
    private const uint SetTopologyFlags = SetTopologyFlag.CurrentGpuTopology | SetTopologyFlag.NoDriverReload;

    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<int, sbyte*, int> _getErrorMessage;
    private readonly delegate* unmanaged[Cdecl]<void*, uint*, int> _enumDisplayGrids;
    private readonly delegate* unmanaged[Cdecl]<void*, uint, uint, int> _setDisplayGrids;
    private readonly delegate* unmanaged[Cdecl]<uint, void*, void*, uint, int> _validateDisplayGrids;
    private readonly delegate* unmanaged[Cdecl]<uint, void*, int> _getDisplayIdInfo;
    private readonly delegate* unmanaged[Cdecl]<int> _unload;
    private bool _disposed;

    private NvApi(
        nint library,
        delegate* unmanaged[Cdecl]<int, sbyte*, int> getErrorMessage,
        delegate* unmanaged[Cdecl]<void*, uint*, int> enumDisplayGrids,
        delegate* unmanaged[Cdecl]<void*, uint, uint, int> setDisplayGrids,
        delegate* unmanaged[Cdecl]<uint, void*, void*, uint, int> validateDisplayGrids,
        delegate* unmanaged[Cdecl]<uint, void*, int> getDisplayIdInfo,
        delegate* unmanaged[Cdecl]<int> unload)
    {
        _library = library;
        _getErrorMessage = getErrorMessage;
        _enumDisplayGrids = enumDisplayGrids;
        _setDisplayGrids = setDisplayGrids;
        _validateDisplayGrids = validateDisplayGrids;
        _getDisplayIdInfo = getDisplayIdInfo;
        _unload = unload;
    }

    /// <summary>
    /// Loads NVAPI and initialises it. Returns null on any machine without a 64-bit NVIDIA driver, without an entry
    /// point we need, or when the library refuses to initialise - all of which simply mean "no Surround here".
    /// </summary>
    internal static NvApi? TryOpen(out string? failure)
    {
        failure = null;
        // The driver puts it into System32. By full path: a bare name would also be looked for next to the EXE, in the
        // current directory and along PATH - on a machine without an NVIDIA driver, that is where a planted DLL would win.
        string path = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
        if (!NativeLibrary.TryLoad(path, out nint library))
        {
            return null;
        }

        bool keep = false;
        try
        {
            if (!NativeLibrary.TryGetExport(library, "nvapi_QueryInterface", out nint queryInterface))
            {
                failure = "nvapi64.dll has no nvapi_QueryInterface entry point.";
                return null;
            }

            var query = (delegate* unmanaged[Cdecl]<uint, void*>)queryInterface;
            void* initialize = query(FunctionId.Initialize);
            void* enumGrids = query(FunctionId.MosaicEnumDisplayGrids);
            void* setGrids = query(FunctionId.MosaicSetDisplayGrids);
            void* validateGrids = query(FunctionId.MosaicValidateDisplayGrids);
            if (initialize is null || enumGrids is null || setGrids is null || validateGrids is null)
            {
                failure = "The installed NVIDIA driver does not offer the Mosaic entry points.";
                return null;
            }

            int status = ((delegate* unmanaged[Cdecl]<int>)initialize)();
            if (status != Status.Ok)
            {
                failure = string.Create(CultureInfo.InvariantCulture, $"NvAPI_Initialize answered {status}.");
                return null;
            }

            keep = true;
            return new NvApi(
                library,
                (delegate* unmanaged[Cdecl]<int, sbyte*, int>)query(FunctionId.GetErrorMessage),
                (delegate* unmanaged[Cdecl]<void*, uint*, int>)enumGrids,
                (delegate* unmanaged[Cdecl]<void*, uint, uint, int>)setGrids,
                (delegate* unmanaged[Cdecl]<uint, void*, void*, uint, int>)validateGrids,
                (delegate* unmanaged[Cdecl]<uint, void*, int>)query(FunctionId.DispGetDisplayIdInfo),
                (delegate* unmanaged[Cdecl]<int>)query(FunctionId.Unload));
        }
        finally
        {
            if (!keep)
            {
                NativeLibrary.Free(library);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_unload is not null)
        {
            _ = _unload();
        }

        NativeLibrary.Free(_library);
    }

    /// <summary>The driver's own wording for a status code, or the bare number when it has none.</summary>
    internal string Describe(int status)
    {
        if (_getErrorMessage is not null)
        {
            sbyte* buffer = stackalloc sbyte[ShortStringMax];
            buffer[0] = 0;
            if (_getErrorMessage(status, buffer) == Status.Ok)
            {
                string? text = Marshal.PtrToStringAnsi((nint)buffer);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return string.Create(CultureInfo.InvariantCulture, $"{text} ({status})");
                }
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"NVAPI status {status}");
    }

    /// <summary>
    /// Every grid the driver knows, written into <paramref name="grids"/>. A display that is not part of a Surround
    /// grid is reported as its own 1x1 grid, so this doubles as the list of available displays.
    /// </summary>
    internal int EnumDisplayGrids(Span<MosaicGridTopoV2> grids, out uint count)
    {
        count = 0;
        uint capacity = (uint)grids.Length;
        for (int i = 0; i < grids.Length; i++)
        {
            grids[i] = default;
            grids[i].Version = MosaicGridTopoV2.StructVersion;
        }

        fixed (MosaicGridTopoV2* first = grids)
        {
            int status = _enumDisplayGrids(first, &capacity);
            if (status == Status.Ok)
            {
                count = Math.Min(capacity, (uint)grids.Length);
            }

            return status;
        }
    }

    /// <summary>How many grids the driver would report, asked without a buffer.</summary>
    internal int CountDisplayGrids(out uint count)
    {
        uint value = 0;
        int status = _enumDisplayGrids(null, &value);
        count = status == Status.Ok ? value : 0;
        return status;
    }

    /// <summary>
    /// Checks grids without changing anything, one verdict per grid in <paramref name="verdicts"/>. Error flags other
    /// than zero mean that grid is not possible.
    /// </summary>
    internal int ValidateDisplayGrids(Span<MosaicGridTopoV2> grids, Span<MosaicDisplayTopoStatus> verdicts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(verdicts.Length, grids.Length, nameof(verdicts));
        for (int i = 0; i < grids.Length; i++)
        {
            verdicts[i] = default;
            verdicts[i].Version = MosaicDisplayTopoStatus.StructVersion;
        }

        fixed (MosaicGridTopoV2* first = grids)
        fixed (MosaicDisplayTopoStatus* status = verdicts)
        {
            return _validateDisplayGrids(SetTopologyFlags, first, status, (uint)grids.Length);
        }
    }

    /// <summary>Replaces the grids that use these displays. Singleton grids for every display remove Surround.</summary>
    internal int SetDisplayGrids(Span<MosaicGridTopoV2> grids)
    {
        fixed (MosaicGridTopoV2* first = grids)
        {
            return _setDisplayGrids(first, (uint)grids.Length, SetTopologyFlags);
        }
    }

    /// <summary>
    /// The Windows adapter and target a display id belongs to, so a driver display id can be matched with the display
    /// list Windows reports. Returns false when the driver is too old for this call (it arrived with R530).
    /// </summary>
    internal bool TryGetDisplayTarget(uint displayId, out uint adapterLow, out int adapterHigh, out uint targetId)
    {
        adapterLow = 0;
        adapterHigh = 0;
        targetId = 0;
        if (_getDisplayIdInfo is null)
        {
            return false;
        }

        DisplayIdInfo info = default;
        info.Version = DisplayIdInfo.StructVersion;
        if (_getDisplayIdInfo(displayId, &info) != Status.Ok)
        {
            return false;
        }

        adapterLow = info.AdapterLuidLow;
        adapterHigh = info.AdapterLuidHigh;
        targetId = info.TargetId;
        return true;
    }

    /// <summary>MAKE_NVAPI_VERSION: the structure's size with its version number in the upper half.</summary>
    internal static uint MakeVersion<T>(int version)
        where T : unmanaged => (uint)Unsafe.SizeOf<T>() | ((uint)version << 16);

    private static class FunctionId
    {
        internal const uint Initialize = 0x0150E828;
        internal const uint Unload = 0xD22BDD7E;
        internal const uint GetErrorMessage = 0x6C2D048C;
        internal const uint DispGetDisplayIdInfo = 0xBAE8AA5E;
        internal const uint MosaicSetDisplayGrids = 0x4D959A89;
        internal const uint MosaicValidateDisplayGrids = 0xCF43903D;
        internal const uint MosaicEnumDisplayGrids = 0xDF2887AF;
    }

    /// <summary>NvAPI_Status values this code reacts to. Everything else is passed on as a number.</summary>
    internal static class Status
    {
        internal const int Ok = 0;
        internal const int NoImplementation = -3;
        internal const int EndEnumeration = -7;
        internal const int IncompatibleStructVersion = -9;
        internal const int DataNotFound = -121;
        internal const int ModeChangeFailed = -149;
        internal const int DriverReloadRequired = -157;
    }

    private static class SetTopologyFlag
    {
        internal const uint CurrentGpuTopology = 1;
        internal const uint NoDriverReload = 2;
    }
}
