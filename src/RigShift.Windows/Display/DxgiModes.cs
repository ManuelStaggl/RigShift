using System.Runtime.InteropServices;
using RigShift.Core.Profiles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace RigShift.Windows.Display;

/// <summary>
/// Refresh rates an active output offers, from DXGI. Unlike EnumDisplaySettings, DXGI reports them as the exact
/// rationals CCD uses (239761/1000 rather than 239), so a chosen rate round-trips through SetDisplayConfig.
/// </summary>
internal static class DxgiModes
{
    public static unsafe IReadOnlyList<RefreshRate> RefreshRates(string gdiName, int width, int height)
    {
        Guid factoryId = typeof(IDXGIFactory1).GUID;
        PInvoke.CreateDXGIFactory1(&factoryId, out object factoryObject).ThrowOnFailure();
        var factory = (IDXGIFactory1)factoryObject;
        try
        {
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Succeeded; a++)
            {
                try
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Succeeded; o++)
                    {
                        try
                        {
                            if (string.Equals(output.GetDesc().DeviceName.ToString(), gdiName, StringComparison.OrdinalIgnoreCase))
                            {
                                return Collect(output, width, height);
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(output);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }

        return [];
    }

    private static List<RefreshRate> Collect(IDXGIOutput output, int width, int height)
    {
        uint count = 0;
        output.GetDisplayModeList(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, 0, ref count, []);
        var modes = new DXGI_MODE_DESC[count];
        output.GetDisplayModeList(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, 0, ref count, modes);

        var rates = new List<RefreshRate>();
        foreach (DXGI_MODE_DESC mode in modes.AsSpan(0, (int)count))
        {
            var rate = new RefreshRate(mode.RefreshRate.Numerator, mode.RefreshRate.Denominator);
            if (mode.Width == width && mode.Height == height && rate.Hertz > 0 && !rates.Any(r => r.LooksLike(rate)))
            {
                rates.Add(rate);
            }
        }

        return [.. rates.OrderByDescending(r => r.Hertz)];
    }
}
