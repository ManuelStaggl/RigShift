using System.ComponentModel;
using RigShift.Core.Profiles;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace RigShift.Windows.Display;

/// <summary>Thin wrappers around the CCD query functions.</summary>
internal static unsafe class CcdNative
{
    public static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) QueryAllPaths()
    {
        // The topology can change between the size query and the query itself; retry on a too-small buffer.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            WIN32_ERROR sizeError = PInvoke.GetDisplayConfigBufferSizes(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ALL_PATHS, out uint pathCount, out uint modeCount);
            if (sizeError != WIN32_ERROR.ERROR_SUCCESS)
            {
                throw new Win32Exception((int)sizeError, $"GetDisplayConfigBufferSizes failed with {sizeError}.");
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            WIN32_ERROR error = PInvoke.QueryDisplayConfig(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ALL_PATHS, ref pathCount, paths, ref modeCount, modes);

            if (error == WIN32_ERROR.ERROR_SUCCESS)
            {
                return (paths[..(int)pathCount], modes[..(int)modeCount]);
            }

            if (error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                throw new Win32Exception((int)error, $"QueryDisplayConfig failed with {error}.");
            }
        }

        throw new Win32Exception((int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER, "QueryDisplayConfig kept reporting a too-small buffer.");
    }

    public static bool TryGetTargetName(LUID adapter, uint targetId, out DISPLAYCONFIG_TARGET_DEVICE_NAME name, out int error)
    {
        name = default;
        name.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        name.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
        name.header.adapterId = adapter;
        name.header.id = targetId;

        fixed (DISPLAYCONFIG_TARGET_DEVICE_NAME* request = &name)
        {
            error = PInvoke.DisplayConfigGetDeviceInfo(&request->header);
        }

        return error == 0;
    }

    public static bool TryGetAdapterPath(LUID adapter, out string path, out int error)
    {
        DISPLAYCONFIG_ADAPTER_NAME name = default;
        name.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME;
        name.header.size = (uint)sizeof(DISPLAYCONFIG_ADAPTER_NAME);
        name.header.adapterId = adapter;

        error = PInvoke.DisplayConfigGetDeviceInfo(&name.header);
        path = error == 0 ? name.adapterDevicePath.ToString() : string.Empty;
        return error == 0;
    }

    /// <summary>GDI name of a source, e.g. <c>\\.\DISPLAY1</c> – the key DXGI outputs are known by.</summary>
    public static bool TryGetSourceGdiName(LUID adapter, uint sourceId, out string gdiName)
    {
        DISPLAYCONFIG_SOURCE_DEVICE_NAME name = default;
        name.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        name.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
        name.header.adapterId = adapter;
        name.header.id = sourceId;

        int error = PInvoke.DisplayConfigGetDeviceInfo(&name.header);
        gdiName = error == 0 ? name.viewGdiDeviceName.ToString() : string.Empty;
        return error == 0;
    }

    /// <summary>
    /// HDR state of a target: <c>true</c>/<c>false</c>, or <c>null</c> when it does not support HDR or cannot be asked.
    /// Windows 11 24H2 and later answer the _2 request, which tells HDR apart from wide color (auto color management);
    /// older versions only know "advanced color", which there means HDR.
    /// </summary>
    public static bool? TryGetHdr(LUID adapter, uint targetId)
    {
        DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 info2 = default;
        info2.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2;
        info2.header.size = (uint)sizeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2);
        info2.header.adapterId = adapter;
        info2.header.id = targetId;
        if (PInvoke.DisplayConfigGetDeviceInfo(&info2.header) == 0)
        {
            return info2.highDynamicRangeSupported ? info2.highDynamicRangeUserEnabled : null;
        }

        DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO info = default;
        info.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
        info.header.size = (uint)sizeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO);
        info.header.adapterId = adapter;
        info.header.id = targetId;
        return PInvoke.DisplayConfigGetDeviceInfo(&info.header) == 0 && info.advancedColorSupported
            ? info.advancedColorEnabled
            : null;
    }

    /// <summary>
    /// Sets HDR. When the _2 query answers (Windows 11 24H2 and later), only the 24H2 request is used and its error is
    /// returned as is; the older advanced color request is used only when that query fails (analysis finding G-02).
    /// </summary>
    public static HdrSetResult SetHdr(LUID adapter, uint targetId, bool enabled)
    {
        DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 info2 = default;
        info2.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2;
        info2.header.size = (uint)sizeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2);
        info2.header.adapterId = adapter;
        info2.header.id = targetId;
        int queryError = PInvoke.DisplayConfigGetDeviceInfo(&info2.header);
        if (queryError != 0)
        {
            return new HdrSetResult(queryError, SetAdvancedColor(adapter, targetId, enabled), UsedLegacyRequest: true);
        }

        DISPLAYCONFIG_SET_HDR_STATE state = default;
        state.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE;
        state.header.size = (uint)sizeof(DISPLAYCONFIG_SET_HDR_STATE);
        state.header.adapterId = adapter;
        state.header.id = targetId;
        state.enableHdr = enabled;
        return new HdrSetResult(queryError, PInvoke.DisplayConfigSetDeviceInfo(&state.header), UsedLegacyRequest: false);
    }

    private static int SetAdvancedColor(LUID adapter, uint targetId, bool enabled)
    {
        DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE legacy = default;
        legacy.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE;
        legacy.header.size = (uint)sizeof(DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE);
        legacy.header.adapterId = adapter;
        legacy.header.id = targetId;
        legacy.enableAdvancedColor = enabled;
        return PInvoke.DisplayConfigSetDeviceInfo(&legacy.header);
    }
}

/// <summary>Native codes of one HDR change: the _2 query that picks the request, and the request itself.</summary>
internal readonly record struct HdrSetResult(int QueryError, int Result, bool UsedLegacyRequest);

/// <summary>Conversions between CCD mode structs and the profile model.</summary>
internal static class CcdModes
{
    public static DisplayAssignment ToAssignment(
        DisplayIdentity identity, DISPLAYCONFIG_SOURCE_MODE source, DISPLAYCONFIG_RATIONAL refresh, DISPLAYCONFIG_ROTATION rotation) =>
        new()
        {
            Identity = identity,
            Width = (int)source.width,
            Height = (int)source.height,
            RefreshNumerator = refresh.Numerator,
            RefreshDenominator = refresh.Denominator,
            PositionX = source.position.x,
            PositionY = source.position.y,
            Rotation = Enum.IsDefined((DisplayRotation)(int)rotation) ? (DisplayRotation)(int)rotation : DisplayRotation.Identity,
            IsPrimary = source.position.x == 0 && source.position.y == 0,
        };
}
