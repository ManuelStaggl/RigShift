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
}

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
