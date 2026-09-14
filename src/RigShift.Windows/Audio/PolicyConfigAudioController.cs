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
/// <c>IPolicyConfig</c> COM interface (default endpoint). The Core Audio objects are free-threaded.
/// </summary>
public sealed class PolicyConfigAudioController : IAudioController
{
    private const int ElementNotFound = unchecked((int)0x80070490);

    private readonly ILogger _log;

    public PolicyConfigAudioController(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<PolicyConfigAudioController>();
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> ListAsync(AudioDirection direction, CancellationToken cancellationToken)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            EDataFlow flow = ToFlow(direction);
            string? defaultId = TryGetDefaultId(enumerator, flow);

            enumerator.EnumAudioEndpoints(
                flow,
                DEVICE_STATE.DEVICE_STATE_ACTIVE | DEVICE_STATE.DEVICE_STATE_DISABLED | DEVICE_STATE.DEVICE_STATE_UNPLUGGED,
                out IMMDeviceCollection collection);
            collection.GetCount(out uint count);

            var devices = new List<AudioDeviceInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out IMMDevice device);
                string id = GetId(device);
                device.GetState(out DEVICE_STATE state);
                devices.Add(new AudioDeviceInfo(
                    new AudioEndpoint(id, GetFriendlyName(device)),
                    direction,
                    IsActive: state == DEVICE_STATE.DEVICE_STATE_ACTIVE,
                    IsDefault: string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
            }

            _log.Debug("Listed {Count} {Direction} endpoints", devices.Count, direction);
            return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(devices);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public Task<bool> SetDefaultAsync(AudioEndpoint endpoint, AudioRoleMask roles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!IsActive(endpoint))
        {
            return Task.FromResult(false);
        }

        var client = new PolicyConfigClient();
        try
        {
            var policy = (IPolicyConfig)client;
            foreach ((AudioRoleMask flag, ERole role) in (ReadOnlySpan<(AudioRoleMask, ERole)>)
                [(AudioRoleMask.Console, ERole.eConsole), (AudioRoleMask.Multimedia, ERole.eMultimedia), (AudioRoleMask.Communications, ERole.eCommunications)])
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
        return Task.FromResult(true);
    }

    public Task SetVolumeAsync(AudioEndpoint endpoint, int percent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            IAudioEndpointVolume volume = ActivateVolume(enumerator, endpoint);
            volume.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, Guid.Empty);
            _log.Information("Volume of {Device} set to {Volume} %", endpoint.FriendlyName, percent);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return Task.CompletedTask;
    }

    public Task<int> GetVolumeAsync(AudioEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            IAudioEndpointVolume volume = ActivateVolume(enumerator, endpoint);
            volume.GetMasterVolumeLevelScalar(out float level);
            int percent = (int)Math.Round(level * 100);
            _log.Debug("Volume of {Device} is {Volume} %", endpoint.FriendlyName, percent);
            return Task.FromResult(percent);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static IAudioEndpointVolume ActivateVolume(IMMDeviceEnumerator enumerator, AudioEndpoint endpoint)
    {
        enumerator.GetDevice(endpoint.EndpointId, out IMMDevice device);
        device.Activate(typeof(IAudioEndpointVolume).GUID, CLSCTX.CLSCTX_ALL, null, out object activated);
        return (IAudioEndpointVolume)activated;
    }

    private bool IsActive(AudioEndpoint endpoint)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            enumerator.GetDevice(endpoint.EndpointId, out IMMDevice device);
            device.GetState(out DEVICE_STATE state);
            if (state != DEVICE_STATE.DEVICE_STATE_ACTIVE)
            {
                _log.Warning("Audio device {Device} is in state {State}, not setting it as default", endpoint.FriendlyName, state);
                return false;
            }

            return true;
        }
        catch (COMException ex) when (ex.HResult == ElementNotFound)
        {
            _log.Warning("Audio device {Device} ({EndpointId}) is not known on this machine", endpoint.FriendlyName, endpoint.EndpointId);
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private string? TryGetDefaultId(IMMDeviceEnumerator enumerator, EDataFlow flow)
    {
        try
        {
            enumerator.GetDefaultAudioEndpoint(flow, ERole.eConsole, out IMMDevice device);
            return GetId(device);
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

    private static EDataFlow ToFlow(AudioDirection direction) =>
        direction == AudioDirection.Capture ? EDataFlow.eCapture : EDataFlow.eRender;
}
