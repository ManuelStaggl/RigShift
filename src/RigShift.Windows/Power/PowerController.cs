using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.System.Threading;

namespace RigShift.Windows.Power;

/// <summary>
/// <see cref="IPowerController"/> over the power management API. Keep-awake is a power request (display + system
/// required) instead of <c>SetThreadExecutionState</c>: it is bound to a handle, not to the calling thread, and shows
/// up in <c>powercfg /requests</c> under RigShift.
/// </summary>
public sealed class PowerController : IPowerController, IDisposable
{
    /// <summary>POWER_REQUEST_CONTEXT_VERSION from minwinbase.h; CsWin32 does not generate it.</summary>
    private const uint PowerRequestContextVersion = 0;

    private const string Reason ="RigShift: the active profile keeps the PC awake";

    private readonly Lock _gate = new();
    private readonly ILogger _log;
    private SafeFileHandle? _request;
    private bool _keepingAwake;

    public PowerController(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<PowerController>();
    }

    public bool IsKeepingAwake
    {
        get
        {
            lock (_gate)
            {
                return _keepingAwake;
            }
        }
    }

    public unsafe IReadOnlyList<PowerPlan> ListPlans()
    {
        var plans = new List<PowerPlan>();
        Span<byte> buffer = stackalloc byte[sizeof(Guid)];
        for (uint index = 0; ; index++)
        {
            uint size = (uint)buffer.Length;
            WIN32_ERROR result = PInvoke.PowerEnumerate(null, null, null, POWER_DATA_ACCESSOR.ACCESS_SCHEME, index, buffer, ref size);
            if (result == WIN32_ERROR.ERROR_NO_MORE_ITEMS)
            {
                return plans;
            }

            ThrowOnError(result, "PowerEnumerate");
            var id = new Guid(buffer);
            plans.Add(new PowerPlan(id, FriendlyName(id)));
        }
    }

    public unsafe Guid GetActivePlan()
    {
        ThrowOnError(PInvoke.PowerGetActiveScheme(null, out Guid* active), "PowerGetActiveScheme");
        try
        {
            return *active;
        }
        finally
        {
            PInvoke.LocalFree(new HLOCAL(active));
        }
    }

    public void SetActivePlan(Guid planId)
    {
        ThrowOnError(PInvoke.PowerSetActiveScheme(null, planId), "PowerSetActiveScheme");
        _log.Information("Power plan {Plan} activated", planId);
    }

    public void SetKeepAwake(bool keepAwake)
    {
        lock (_gate)
        {
            if (keepAwake == _keepingAwake)
            {
                return;
            }

            SafeFileHandle request = _request ??= CreateRequest();
            foreach (POWER_REQUEST_TYPE type in (ReadOnlySpan<POWER_REQUEST_TYPE>)[POWER_REQUEST_TYPE.PowerRequestDisplayRequired, POWER_REQUEST_TYPE.PowerRequestSystemRequired])
            {
                bool done = keepAwake ? PInvoke.PowerSetRequest(request, type) : PInvoke.PowerClearRequest(request, type);
                if (!done)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"{(keepAwake ? "PowerSetRequest" : "PowerClearRequest")}({type}) failed.");
                }
            }

            _keepingAwake = keepAwake;
            _log.Information(keepAwake ? "Keeping the PC and displays awake" : "No longer keeping the PC awake");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _request?.Dispose();
            _request = null;
            _keepingAwake = false;
        }
    }

    private static unsafe SafeFileHandle CreateRequest()
    {
        fixed (char* reason = Reason)
        {
            var context = new REASON_CONTEXT
            {
                Version = PowerRequestContextVersion,
                Flags = POWER_REQUEST_CONTEXT_FLAGS.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            };
            context.Reason.SimpleReasonString = new PWSTR(reason);

            SafeFileHandle handle = PInvoke.PowerCreateRequest(context);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error, "PowerCreateRequest failed.");
            }

            return handle;
        }
    }

    private static string FriendlyName(Guid planId)
    {
        uint size = 0;
        if (PInvoke.PowerReadFriendlyName(null, planId, null, null, [], ref size) != WIN32_ERROR.ERROR_SUCCESS || size == 0)
        {
            return planId.ToString("D");
        }

        byte[] buffer = new byte[size];
        if (PInvoke.PowerReadFriendlyName(null, planId, null, null, buffer, ref size) != WIN32_ERROR.ERROR_SUCCESS)
        {
            return planId.ToString("D");
        }

        string name = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        return name.Length > 0 ? name : planId.ToString("D");
    }

    private static void ThrowOnError(WIN32_ERROR result, string function)
    {
        if (result != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new Win32Exception((int)result, $"{function} failed with {result}.");
        }
    }
}
