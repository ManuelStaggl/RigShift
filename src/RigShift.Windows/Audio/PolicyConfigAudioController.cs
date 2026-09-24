using System.Runtime.InteropServices;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace RigShift.Windows.Audio;

/// <summary>
/// <see cref="IAudioController"/> using Core Audio (enumeration, state, volume) and the undocumented
/// <c>IPolicyConfig</c> COM interface (default endpoint). The Core Audio objects are free-threaded, so every call runs
/// on a pool thread and the pages can await it from the UI thread.
/// </summary>
public sealed class PolicyConfigAudioController : IAudioController
{
    private const int ElementNotFound = unchecked((int)0x80070490);

    private static readonly (AudioRoleMask Flag, ERole Role)[] Roles =
    [
        (AudioRoleMask.Console, ERole.eConsole),
        (AudioRoleMask.Multimedia, ERole.eMultimedia),
        (AudioRoleMask.Communications, ERole.eCommunications),
    ];

    private readonly ILogger _log;

    public PolicyConfigAudioController(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<PolicyConfigAudioController>();
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> ListAsync(AudioDirection direction, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<AudioDeviceInfo>>(() => List(direction), cancellationToken);

    public Task<bool> SetDefaultAsync(AudioEndpoint endpoint, AudioRoleMask roles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return Task.Run(() => SetDefault(endpoint, roles), cancellationToken);
    }

    public Task SetVolumeAsync(AudioEndpoint endpoint, int percent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return Task.Run(() => SetVolume(endpoint, percent), cancellationToken);
    }

    public Task<int> GetVolumeAsync(AudioEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return Task.Run(() => GetVolume(endpoint), cancellationToken);
    }

    private List<AudioDeviceInfo> List(AudioDirection direction)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            EDataFlow flow = ToFlow(direction);
            (AudioRoleMask Flag, string? Id)[] defaults = [.. Roles.Select(r => (r.Flag, TryGetDefaultId(enumerator, flow, r.Role)))];

            enumerator.EnumAudioEndpoints(
                flow,
                DEVICE_STATE.DEVICE_STATE_ACTIVE | DEVICE_STATE.DEVICE_STATE_DISABLED | DEVICE_STATE.DEVICE_STATE_UNPLUGGED,
                out IMMDeviceCollection collection);
            try
            {
                collection.GetCount(out uint count);

                var devices = new List<AudioDeviceInfo>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    collection.Item(i, out IMMDevice device);
                    try
                    {
                        string id = GetId(device);
                        device.GetState(out DEVICE_STATE state);
                        devices.Add(new AudioDeviceInfo(
                            new AudioEndpoint(id, GetFriendlyName(device)),
                            direction,
                            IsActive: state == DEVICE_STATE.DEVICE_STATE_ACTIVE,
                            DefaultRoles: defaults
                                .Where(d => string.Equals(id, d.Id, StringComparison.OrdinalIgnoreCase))
                                .Aggregate(AudioRoleMask.None, (mask, d) => mask | d.Flag)));
                    }
                    finally
                    {
                        Release(device);
                    }
                }

                _log.Debug("Listed {Count} {Direction} endpoints", devices.Count, direction);
                return devices;
            }
            finally
            {
                Release(collection);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private bool SetDefault(AudioEndpoint endpoint, AudioRoleMask roles)
    {
        if (!IsActive(endpoint))
        {
            return false;
        }

        var client = new PolicyConfigClient();
        try
        {
            var policy = (IPolicyConfig)client;
            foreach ((AudioRoleMask flag, ERole role) in Roles)
            {
                if (roles.HasFlag(flag))
                {
                    int hr = policy.SetDefaultEndpoint(endpoint.EndpointId, (int)role);
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(client);
        }

        _log.Information("Default {Roles} endpoint set to {Device}", roles, endpoint.FriendlyName);
        return true;
    }

    private void SetVolume(AudioEndpoint endpoint, int percent)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            IAudioEndpointVolume volume = ActivateVolume(enumerator, endpoint);
            try
            {
                volume.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, Guid.Empty);
            }
            finally
            {
                Release(volume);
            }

            _log.Information("Volume of {Device} set to {Volume} %", endpoint.FriendlyName, percent);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private int GetVolume(AudioEndpoint endpoint)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            IAudioEndpointVolume volume = ActivateVolume(enumerator, endpoint);
            float level;
            try
            {
                volume.GetMasterVolumeLevelScalar(out level);
            }
            finally
            {
                Release(volume);
            }

            int percent = (int)Math.Round(level * 100);
            _log.Debug("Volume of {Device} is {Volume} %", endpoint.FriendlyName, percent);
            return percent;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static IAudioEndpointVolume ActivateVolume(IMMDeviceEnumerator enumerator, AudioEndpoint endpoint)
    {
        enumerator.GetDevice(endpoint.EndpointId, out IMMDevice device);
        try
        {
            device.Activate(typeof(IAudioEndpointVolume).GUID, CLSCTX.CLSCTX_ALL, null, out object activated);
            return (IAudioEndpointVolume)activated;
        }
        finally
        {
            Release(device);
        }
    }

    private bool IsActive(AudioEndpoint endpoint)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            enumerator.GetDevice(endpoint.EndpointId, out IMMDevice device);
            DEVICE_STATE state;
            try
            {
                device.GetState(out state);
            }
            finally
            {
                Release(device);
            }

            if (state != DEVICE_STATE.DEVICE_STATE_ACTIVE)
            {
                _log.Warning("Audio device {Device} is in state {State}, not setting it as default", endpoint.FriendlyName, state);
                return false;
            }

            return true;
        }
        catch (COMException ex) when (ex.HResult == ElementNotFound)
        {
            _log.Warning("Audio device {Device} is not known on this machine", endpoint.FriendlyName);
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private string? TryGetDefaultId(IMMDeviceEnumerator enumerator, EDataFlow flow, ERole role)
    {
        try
        {
            enumerator.GetDefaultAudioEndpoint(flow, role, out IMMDevice device);
            try
            {
                return GetId(device);
            }
            finally
            {
                Release(device);
            }
        }
        catch (COMException ex) when (ex.HResult == ElementNotFound)
        {
            _log.Debug("No default {Flow} endpoint", flow);
            return null;
        }
    }

    private static unsafe string GetId(IMMDevice device)
    {
        device.GetId(out PWSTR id);
        try
        {
            return id.ToString();
        }
        finally
        {
            PInvoke.CoTaskMemFree(id.Value);
        }
    }

    private static string GetFriendlyName(IMMDevice device)
    {
        device.OpenPropertyStore(STGM.STGM_READ, out IPropertyStore store);
        try
        {
            store.GetValue(in PInvoke.PKEY_Device_FriendlyName, out PROPVARIANT value);
            try
            {
                return value.vt == VARENUM.VT_LPWSTR ? value.pwszVal.ToString() : string.Empty;
            }
            finally
            {
                PInvoke.PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    /// <summary>Releases the RCW now instead of at the next GC (analysis finding G-01).</summary>
    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    private static EDataFlow ToFlow(AudioDirection direction) =>
        direction == AudioDirection.Capture ? EDataFlow.eCapture : EDataFlow.eRender;
}
