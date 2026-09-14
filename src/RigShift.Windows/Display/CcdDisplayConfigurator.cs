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

    public Task<DisplaySnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) = CcdNative.QueryAllPaths();
        var adapterPaths = new Dictionary<AdapterLuid, string>();
        var targets = new Dictionary<(AdapterLuid, uint), TargetAccumulator>();

        foreach (DISPLAYCONFIG_PATH_INFO path in paths)
        {
            var adapter = AdapterLuid.From(path.targetInfo.adapterId);
            if (!targets.TryGetValue((adapter, path.targetInfo.id), out TargetAccumulator? target))
            {
                target = new TargetAccumulator(adapter, path.targetInfo.id);
                targets.Add((adapter, path.targetInfo.id), target);
            }

            var source = new CcdSource(AdapterLuid.From(path.sourceInfo.adapterId), path.sourceInfo.id);
            if (!target.Sources.Contains(source))
            {
                target.Sources.Add(source);
            }

            target.Available |= path.targetInfo.targetAvailable;
            if ((path.flags & PInvoke.DISPLAYCONFIG_PATH_ACTIVE) != 0)
            {
                target.ActivePath = path;
            }
        }

        var displays = new List<AttachedDisplay>();
        foreach (TargetAccumulator target in targets.Values)
        {
            if (!CcdNative.TryGetTargetName(target.Adapter.ToLuid(), target.TargetId, out DISPLAYCONFIG_TARGET_DEVICE_NAME name, out int error))
            {
                _log.Debug("Target {Adapter}:{TargetId} has no device name (error {Error}), skipped", target.Adapter, target.TargetId, error);
                continue;
            }

            string monitorPath = name.monitorDevicePath.ToString();
            if (monitorPath.Length == 0)
            {
                // Connector without a known monitor – nothing a profile could refer to.
                continue;
            }

            if (!adapterPaths.TryGetValue(target.Adapter, out string? adapterPath))
            {
                adapterPath = CcdNative.TryGetAdapterPath(target.Adapter.ToLuid(), out string path, out int adapterError) ? path : string.Empty;
                if (adapterPath.Length == 0)
                {
                    _log.Warning("Adapter {Adapter} has no device path (error {Error})", target.Adapter, adapterError);
                }

                adapterPaths.Add(target.Adapter, adapterPath);
            }

            var identity = new DisplayIdentity
            {
                AdapterDevicePath = adapterPath,
                TargetDevicePath = monitorPath,
                EdidManufacturerId = name.flags.edidIdsValid ? name.edidManufactureId : (ushort)0,
                EdidProductCodeId = name.flags.edidIdsValid ? name.edidProductCodeId : (ushort)0,
                FriendlyName = name.monitorFriendlyDeviceName.ToString(),
            };

            CcdSource? activeSource = target.ActivePath is { } active
                ? new CcdSource(AdapterLuid.From(active.sourceInfo.adapterId), active.sourceInfo.id)
                : null;

            displays.Add(new AttachedDisplay
            {
                Identity = identity,
                IsAvailable = target.Available,
                IsActive = target.ActivePath is not null,
                ActiveMode = target.ActivePath is { } activePath ? DecodeActiveMode(identity, activePath, modes) : null,
                NativeHandle = new CcdTargetHandle(target.Adapter, target.TargetId, target.Sources, activeSource),
            });
        }

        // Available targets first: if a monitor shows up on two targets, the planner takes the usable one.
        List<AttachedDisplay> ordered = displays.OrderByDescending(d => d.IsAvailable).ThenByDescending(d => d.IsActive).ToList();
        _log.Information("Snapshot: {Paths} paths, {Displays} displays ({Active} active, {Available} available)",
            paths.Length, ordered.Count, ordered.Count(d => d.IsActive), ordered.Count(d => d.IsAvailable));

        return Task.FromResult(new DisplaySnapshot { TakenAt = _time.GetUtcNow(), Displays = ordered });
    }

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
            (paths, modes) = CcdPathBuilder.Build(displays, options.UseDatabaseModes);
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

        int result = PInvoke.SetDisplayConfig(paths, modes, flags);
        _log.Information("SetDisplayConfig({Paths} paths, {Modes} modes, {Flags}) returned {Result}", paths.Length, modes.Length, flags, result);
        return Task.FromResult(result);
    }

    private static DisplayAssignment? DecodeActiveMode(DisplayIdentity identity, DISPLAYCONFIG_PATH_INFO path, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        uint sourceIndex = path.sourceInfo.modeInfoIdx;
        if (sourceIndex >= modes.Length || modes[sourceIndex].infoType != DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
        {
            return null;
        }

        DISPLAYCONFIG_SOURCE_MODE source = modes[sourceIndex].sourceMode;
        DISPLAYCONFIG_RATIONAL refresh = path.targetInfo.refreshRate;
        uint targetIndex = path.targetInfo.modeInfoIdx;
        if (targetIndex < modes.Length && modes[targetIndex].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
        {
            refresh = modes[targetIndex].targetMode.targetVideoSignalInfo.vSyncFreq;
        }

        return CcdModes.ToAssignment(identity, source, refresh, path.targetInfo.rotation);
    }

    private sealed class TargetAccumulator(AdapterLuid adapter, uint targetId)
    {
        public AdapterLuid Adapter { get; } = adapter;

        public uint TargetId { get; } = targetId;

        public List<CcdSource> Sources { get; } = [];

        public bool Available { get; set; }

        public DISPLAYCONFIG_PATH_INFO? ActivePath { get; set; }
    }
}
