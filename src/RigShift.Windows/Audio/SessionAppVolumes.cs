using System.Diagnostics;
using System.Runtime.InteropServices;
using RigShift.Core.Abstractions;
using Serilog;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace RigShift.Windows.Audio;

/// <summary>
/// <see cref="IAppVolumes"/> through the audio sessions of every active playback device, the same ones the Windows
/// volume mixer shows. Core Audio is free-threaded, so the calls run on whatever thread the caller is on.
/// </summary>
public sealed class SessionAppVolumes : IAppVolumes
{
    private readonly ILogger _log;

    public SessionAppVolumes(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<SessionAppVolumes>();
    }

    public IReadOnlyList<string> AppsWithSound()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        ForEachSession((process, _) => names.Add(process + ".exe"));
        return [.. names];
    }

    public int Adjust(string executable, int volumePercent, bool muted)
    {
        ArgumentNullException.ThrowIfNull(executable);
        string wanted = Path.GetFileNameWithoutExtension(executable);
        float level = Math.Clamp(volumePercent, 0, 100) / 100f;
        int set = 0;
        ForEachSession((process, session) =>
        {
            if (string.Equals(process, wanted, StringComparison.OrdinalIgnoreCase) && session is ISimpleAudioVolume volume)
            {
                volume.SetMasterVolume(level, Guid.Empty);
                volume.SetMute(muted, Guid.Empty);
                set++;
            }
        });

        if (set > 0)
        {
            _log.Information("Volume of {App} set to {Volume} % (muted: {Muted}) in {Sessions} audio session(s)", executable, volumePercent, muted, set);
        }

        return set;
    }

    // The process name of each session (without ".exe") and the session itself; sessions of the system sounds (process
    // 0) and of programs that already ended are skipped. One device that fails is logged and the others still count.
    private void ForEachSession(Action<string, IAudioSessionControl2> visit)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE.DEVICE_STATE_ACTIVE, out devices);
            devices.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                devices.Item(i, out IMMDevice device);
                try
                {
                    VisitDevice(device, visit);
                }
                catch (COMException ex)
                {
                    _log.Warning(ex, "Could not read the audio sessions of one playback device");
                }
                finally
                {
                    Release(device);
                }
            }
        }
        catch (COMException ex)
        {
            _log.Warning(ex, "Could not list the playback devices for the app volumes");
        }
        finally
        {
            Release(devices);
            Release(enumerator);
        }
    }

    private static void VisitDevice(IMMDevice device, Action<string, IAudioSessionControl2> visit)
    {
        device.Activate(typeof(IAudioSessionManager2).GUID, CLSCTX.CLSCTX_ALL, null, out object activated);
        var manager = (IAudioSessionManager2)activated;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            sessions = manager.GetSessionEnumerator();
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                sessions.GetSession(i, out IAudioSessionControl control);
                try
                {
                    if (control is IAudioSessionControl2 session && ProcessName(session) is { } process)
                    {
                        visit(process, session);
                    }
                }
                finally
                {
                    Release(control);
                }
            }
        }
        finally
        {
            Release(sessions);
            Release(manager);
        }
    }

    private static string? ProcessName(IAudioSessionControl2 session)
    {
        session.GetProcessId(out uint id);
        if (id == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)id);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // Ended since the session was listed.
            return null;
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }
}
