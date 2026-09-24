using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace RigShift.Windows.Display;

/// <summary>
/// <see cref="IDisplayConfigurator"/> on top of the Windows CCD API.
/// Reference implementation of the algorithm: <c>legacy/DisplayProfile.ps1</c> (proven on real hardware).
/// Rules that must survive any refactoring are documented in <c>docs/display-topology.md</c>.
/// </summary>
public sealed class CcdDisplayConfigurator : IDisplayConfigurator
{
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    public CcdDisplayConfigurator(ILogger log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        _log = log.ForContext<CcdDisplayConfigurator>();
        _time = time;
    }

    /// <summary>On a pool thread: the query takes a noticeable moment, and the pages ask from the UI thread.</summary>
    public Task<DisplaySnapshot> QueryAsync(CancellationToken cancellationToken) => Task.Run(Query, cancellationToken);

    private DisplaySnapshot Query()
    {
        CcdRawSnapshot raw = QueryRaw(_log);
        List<AttachedDisplay> ordered = CcdSnapshotBuilder.Build(raw, _log);
        // Debug: the tray, the automation and every open page ask, so at Information this line was most of the log.
        _log.Debug("Snapshot: {Paths} paths, {Displays} displays ({Active} active, {Available} available)",
            raw.Paths.Count, ordered.Count, ordered.Count(d => d.IsActive), ordered.Count(d => d.IsAvailable));

        return new DisplaySnapshot { TakenAt = _time.GetUtcNow(), Displays = ordered };
    }

    /// <summary>
    /// Read-only: queries the CCD paths and modes and asks for the names (and HDR state) that <see cref="CcdSnapshotBuilder"/>
    /// needs, plus the serial number fingerprint from each monitor's EDID. Contains device paths of this machine.
    /// </summary>
    public static CcdRawSnapshot QueryRaw(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) = CcdNative.QueryAllPaths();

        var rawPaths = new List<CcdRawPath>(paths.Length);
        var targetKeys = new List<(AdapterLuid Adapter, uint TargetId)>();
        var activeTargets = new HashSet<(AdapterLuid, uint)>();
        var rawSources = new List<CcdRawSource>();
        foreach (DISPLAYCONFIG_PATH_INFO path in paths)
        {
            var targetAdapter = AdapterLuid.From(path.targetInfo.adapterId);
            bool active = (path.flags & PInvoke.DISPLAYCONFIG_PATH_ACTIVE) != 0;
            rawPaths.Add(new CcdRawPath(
                AdapterLuid.From(path.sourceInfo.adapterId).ToString(),
                path.sourceInfo.id,
                path.sourceInfo.modeInfoIdx,
                targetAdapter.ToString(),
                path.targetInfo.id,
                path.targetInfo.modeInfoIdx,
                path.targetInfo.targetAvailable,
                active,
                path.targetInfo.refreshRate.Numerator,
                path.targetInfo.refreshRate.Denominator,
                (int)path.targetInfo.rotation));

            if (!targetKeys.Contains((targetAdapter, path.targetInfo.id)))
            {
                targetKeys.Add((targetAdapter, path.targetInfo.id));
            }

            if (active)
            {
                activeTargets.Add((targetAdapter, path.targetInfo.id));
                string sourceAdapter = AdapterLuid.From(path.sourceInfo.adapterId).ToString();
                if (!rawSources.Any(s => s.Adapter == sourceAdapter && s.SourceId == path.sourceInfo.id)
                    && CcdNative.TryGetSourceGdiName(path.sourceInfo.adapterId, path.sourceInfo.id, out string gdiName))
                {
                    rawSources.Add(new CcdRawSource(sourceAdapter, path.sourceInfo.id, gdiName));
                }
            }
        }

        var rawTargets = new List<CcdRawTarget>(targetKeys.Count);
        var monitorAdapters = new List<AdapterLuid>();
        foreach ((AdapterLuid adapter, uint targetId) in targetKeys)
        {
            if (!CcdNative.TryGetTargetName(adapter.ToLuid(), targetId, out DISPLAYCONFIG_TARGET_DEVICE_NAME name, out int error))
            {
                rawTargets.Add(new CcdRawTarget(adapter.ToString(), targetId, error, string.Empty, string.Empty, false, 0, 0, null));
                continue;
            }

            string monitorPath = name.monitorDevicePath.ToString();
            bool hasMonitor = monitorPath.Length > 0;
            if (hasMonitor && !monitorAdapters.Contains(adapter))
            {
                monitorAdapters.Add(adapter);
            }

            rawTargets.Add(new CcdRawTarget(
                adapter.ToString(),
                targetId,
                0,
                monitorPath,
                name.monitorFriendlyDeviceName.ToString(),
                name.flags.edidIdsValid,
                name.edidManufactureId,
                name.edidProductCodeId,
                hasMonitor && activeTargets.Contains((adapter, targetId)) ? CcdNative.TryGetHdr(adapter.ToLuid(), targetId) : null,
                hasMonitor && Edid.Read(monitorPath, log) is { } edid ? Edid.SerialHash(edid) : null));
        }

        var rawAdapters = monitorAdapters
            .Select(adapter => CcdNative.TryGetAdapterPath(adapter.ToLuid(), out string devicePath, out int error)
                ? new CcdRawAdapter(adapter.ToString(), 0, devicePath)
                : new CcdRawAdapter(adapter.ToString(), error, string.Empty))
            .ToList();

        return new CcdRawSnapshot
        {
            Paths = rawPaths,
            Modes = modes.Select(ToRawMode).ToList(),
            Targets = rawTargets,
            Adapters = rawAdapters,
            Sources = rawSources,
        };
    }

    private static CcdRawMode ToRawMode(DISPLAYCONFIG_MODE_INFO mode) => mode.infoType switch
    {
        DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE => new CcdRawMode(
            CcdModeKind.Source, mode.sourceMode.width, mode.sourceMode.height, mode.sourceMode.position.x, mode.sourceMode.position.y, 0, 0),
        DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET => new CcdRawMode(
            CcdModeKind.Target, 0, 0, 0, 0, mode.targetMode.targetVideoSignalInfo.vSyncFreq.Numerator, mode.targetMode.targetVideoSignalInfo.vSyncFreq.Denominator),
        _ => new CcdRawMode(CcdModeKind.Other, 0, 0, 0, 0, 0, 0),
    };

    public Task<int> ApplyAsync(TopologyPlan plan, ApplyOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        var displays = new List<(CcdTargetHandle, DisplayAssignment)>();
        foreach (PlannedDisplay planned in plan.Resolved)
        {
            if (planned.Target.NativeHandle is not CcdTargetHandle handle)
            {
                _log.Error("Display {Display} has no CCD handle; the plan was not built from a CCD snapshot", DisplayNames.Of(planned.Assignment));
                return Task.FromResult((int)WIN32_ERROR.ERROR_INVALID_PARAMETER);
            }

            displays.Add((handle, planned.Assignment));
        }

        DISPLAYCONFIG_PATH_INFO[] paths;
        DISPLAYCONFIG_MODE_INFO[] modes;
        try
        {
            (paths, modes) = CcdPathBuilder.Build(displays, options.UseDatabaseModes, options.AllowClone);
        }
        catch (InvalidOperationException ex)
        {
            _log.Error(ex, "Could not build the display configuration for {Profile}", plan.Profile.Name);
            return Task.FromResult((int)WIN32_ERROR.ERROR_INVALID_PARAMETER);
        }

        SET_DISPLAY_CONFIG_FLAGS flags = SET_DISPLAY_CONFIG_FLAGS.SDC_APPLY
            | SET_DISPLAY_CONFIG_FLAGS.SDC_USE_SUPPLIED_DISPLAY_CONFIG
            | SET_DISPLAY_CONFIG_FLAGS.SDC_ALLOW_CHANGES;
        if (options.SaveToDatabase)
        {
            flags |= SET_DISPLAY_CONFIG_FLAGS.SDC_SAVE_TO_DATABASE;
        }

        // Logged before the native call and run on its own thread, like HDR below: a driver that freezes inside the call
        // leaves this line as the last trace, and the orchestrator's time limit can give up without blocking a pool thread.
        _log.Information("Calling SetDisplayConfig({Paths} paths, {Modes} modes, {Flags})", paths.Length, modes.Length, flags);
        return Task.Factory.StartNew(
            () =>
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                int result = PInvoke.SetDisplayConfig(paths, modes, flags);
                _log.Information("SetDisplayConfig({Paths} paths, {Modes} modes, {Flags}) returned {Result} after {Milliseconds:0} ms",
                    paths.Length, modes.Length, flags, result, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return result;
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public Task<int> SetHdrAsync(AttachedDisplay display, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (display.NativeHandle is not CcdTargetHandle handle)
        {
            return Task.FromResult((int)WIN32_ERROR.ERROR_INVALID_PARAMETER);
        }

        // Logged before the native call: at HW-12 the call never returned and the log ended without a trace of it. The call
        // runs on its own thread, so the orchestrator's time limit can give up on it without blocking a pool thread.
        _log.Information("Setting HDR of {Display} to {Enabled}", DisplayNames.Of(display.Identity), enabled);
        return Task.Factory.StartNew(
            () =>
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                HdrSetResult result = CcdNative.SetHdr(handle.Adapter.ToLuid(), handle.TargetId, enabled);
                _log.Information("HDR of {Display} set to {Enabled} with the {Request} request: result {Result}, _2 query {QueryError}, after {Milliseconds:0} ms",
                    DisplayNames.Of(display.Identity), enabled, result.UsedLegacyRequest ? "SET_ADVANCED_COLOR_STATE" : "SET_HDR_STATE",
                    result.Result, result.QueryError, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return result.Result;
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public Task<IReadOnlyList<RefreshRate>> ListRefreshRatesAsync(DisplayIdentity identity, int width, int height, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Task.Run(() => ListRefreshRates(identity, width, height), cancellationToken);
    }

    private IReadOnlyList<RefreshRate> ListRefreshRates(DisplayIdentity identity, int width, int height)
    {
        (DISPLAYCONFIG_PATH_INFO[] paths, _) = CcdNative.QueryAllPaths();
        foreach (DISPLAYCONFIG_PATH_INFO path in paths)
        {
            if ((path.flags & PInvoke.DISPLAYCONFIG_PATH_ACTIVE) == 0
                || !CcdNative.TryGetTargetName(path.targetInfo.adapterId, path.targetInfo.id, out DISPLAYCONFIG_TARGET_DEVICE_NAME name, out _)
                || !string.Equals(name.monitorDevicePath.ToString(), identity.TargetDevicePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!CcdNative.TryGetSourceGdiName(path.sourceInfo.adapterId, path.sourceInfo.id, out string gdiName))
            {
                break;
            }

            IReadOnlyList<RefreshRate> rates = DxgiModes.RefreshRates(gdiName, width, height);
            _log.Debug("{Display} offers {Count} refresh rates at {Width}x{Height}", DisplayNames.Of(identity), rates.Count, width, height);
            return rates;
        }

        return [];
    }
}
