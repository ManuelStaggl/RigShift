using System.Runtime.InteropServices;
using Serilog;
using Windows.Win32;
using Windows.Win32.Devices.HumanInterfaceDevice;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;

namespace RigShift.Windows.Input;

/// <summary>
/// Buttons on wheels, button boxes and game controllers while one window listens: the countdown, where the user sits at
/// the rig without a keyboard (v4 finding U-04). Raw Input delivers the reports even while another window has the focus;
/// the HID parser says which buttons they hold. Axes (wheel, pedals) and hat switches are never read.
/// </summary>
public sealed class ControllerButtons : IDisposable
{
    /// <summary>The message Raw Input sends to the listening window. It still has to reach <c>DefWindowProc</c>.</summary>
    public const int WmInput = 0x00FF;

    private const ushort GenericDesktop = 0x01;

    /// <summary>Joystick (most wheels and button boxes), game pad, multi-axis controller.</summary>
    private static readonly ushort[] ControllerUsages = [0x04, 0x05, 0x08];

    private readonly ILogger _log;
    private readonly ButtonChanges _changes = new();
    private readonly Dictionary<nint, Device?> _devices = [];
    private byte[] _input = new byte[512];
    private USAGE_AND_PAGE[] _usages = new USAGE_AND_PAGE[64];
    private uint[] _pressed = new uint[64];
    private bool _registered = true;

    private ControllerButtons(ILogger log) => _log = log;

    /// <summary>Starts listening for <paramref name="hwnd"/>; null when Windows refused – the window then works without.</summary>
    public static ControllerButtons? Listen(nint hwnd, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!Register(new HWND(hwnd), RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK))
        {
            log.Warning("Wheel and controller buttons cannot confirm: Raw Input registration failed with error {Error}",
                Marshal.GetLastPInvokeError());
            return null;
        }

        log.Information("Listening for wheel, button box and controller buttons");
        return new ControllerButtons(log);
    }

    /// <summary>
    /// Whether the <see cref="WmInput"/> message with this <paramref name="lParam"/> changed a button on a device that had
    /// reported before (<see cref="ButtonChanges"/>).
    /// </summary>
    public unsafe bool IsButtonChange(nint lParam)
    {
        var handle = new HRAWINPUT(lParam);
        uint headerSize = (uint)sizeof(RAWINPUTHEADER);
        uint size = 0;
        if (PInvoke.GetRawInputData(handle, RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, null, &size, headerSize) != 0 || size == 0)
        {
            return false;
        }

        if (_input.Length < size)
        {
            _input = new byte[size];
        }

        fixed (byte* buffer = _input)
        {
            uint read = PInvoke.GetRawInputData(handle, RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, buffer, &size, headerSize);
            var input = (RAWINPUT*)buffer;
            if (read == uint.MaxValue || read < headerSize || input->header.dwType != (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEHID)
            {
                return false;
            }

            nint deviceHandle = (nint)input->header.hDevice.Value;
            if (DeviceFor(deviceHandle) is not { } device)
            {
                return false;
            }

            byte* reports = (byte*)&input->data.hid.bRawData;
            long available = (buffer + read) - reports;
            uint reportSize = input->data.hid.dwSizeHid;
            uint count = input->data.hid.dwCount;
            if (reportSize == 0 || available < 0 || (long)reportSize * count > available)
            {
                return false;
            }

            bool changed = false;
            for (uint i = 0; i < count; i++)
            {
                changed |= Evaluate(deviceHandle, device, reports + (i * reportSize), reportSize);
            }

            return changed;
        }
    }

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        if (!Register(HWND.Null, RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE))
        {
            _log.Debug("Raw Input for controller buttons could not be removed (error {Error})", Marshal.GetLastPInvokeError());
        }
    }

    private static unsafe bool Register(HWND target, RAWINPUTDEVICE_FLAGS flags)
    {
        Span<RAWINPUTDEVICE> devices = stackalloc RAWINPUTDEVICE[ControllerUsages.Length];
        for (int i = 0; i < devices.Length; i++)
        {
            devices[i] = new RAWINPUTDEVICE { usUsagePage = GenericDesktop, usUsage = ControllerUsages[i], dwFlags = flags, hwndTarget = target };
        }

        return PInvoke.RegisterRawInputDevices(devices, (uint)sizeof(RAWINPUTDEVICE));
    }

    /// <summary>The buttons one report holds, handed to <see cref="ButtonChanges"/>; its first byte is the report ID (0 without).</summary>
    private unsafe bool Evaluate(nint deviceHandle, Device device, byte* report, uint reportSize)
    {
        uint length = (uint)device.MaxButtons;
        NTSTATUS status;
        fixed (byte* preparsed = device.Preparsed)
        {
            status = PInvoke.HidP_GetUsagesEx(HIDP_REPORT_TYPE.HidP_Input, 0, _usages.AsSpan(0, device.MaxButtons), ref length,
                new PHIDP_PREPARSED_DATA((nint)preparsed), new PSTR(report), reportSize);
        }

        // Anything but success – mostly a report ID that carries no buttons – says nothing about them.
        if (status != NTSTATUS.HIDP_STATUS_SUCCESS)
        {
            return false;
        }

        Span<uint> pressed = _pressed.AsSpan(0, (int)length);
        for (int i = 0; i < pressed.Length; i++)
        {
            pressed[i] = ((uint)_usages[i].UsagePage << 16) | _usages[i].Usage;
        }

        return _changes.Update(deviceHandle, report[0], pressed);
    }

    /// <summary>The HID description of a device, read once; null for one without buttons or one Windows does not describe.</summary>
    private unsafe Device? DeviceFor(nint handle)
    {
        if (_devices.TryGetValue(handle, out Device? known))
        {
            return known;
        }

        Device? device = null;
        uint size = 0;
        var deviceHandle = new HANDLE((void*)handle);
        if (PInvoke.GetRawInputDeviceInfo(deviceHandle, RAW_INPUT_DEVICE_INFO_COMMAND.RIDI_PREPARSEDDATA, null, &size) == 0 && size > 0)
        {
            var preparsed = new byte[size];
            fixed (byte* data = preparsed)
            {
                uint copied = PInvoke.GetRawInputDeviceInfo(deviceHandle, RAW_INPUT_DEVICE_INFO_COMMAND.RIDI_PREPARSEDDATA, data, &size);
                uint buttons = copied is > 0 and not uint.MaxValue
                    ? PInvoke.HidP_MaxUsageListLength(HIDP_REPORT_TYPE.HidP_Input, 0, new PHIDP_PREPARSED_DATA((nint)data))
                    : 0;
                device = buttons > 0 ? new Device(preparsed, (int)buttons) : null;
            }
        }

        if (device is { MaxButtons: var count } && count > _usages.Length)
        {
            _usages = new USAGE_AND_PAGE[count];
            _pressed = new uint[count];
        }

        _log.Information("Controller {Handle:X} reports, {Buttons} buttons", handle, device?.MaxButtons ?? 0);
        _devices[handle] = device;
        return device;
    }

    private sealed record Device(byte[] Preparsed, int MaxButtons);
}
