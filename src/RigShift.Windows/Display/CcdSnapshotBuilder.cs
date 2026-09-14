using System.Globalization;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace RigShift.Windows.Display;

/// <summary>
/// Turns raw CCD input into the displays of a snapshot. No Windows calls, so it runs on fixtures (analysis finding L-04).
/// Rules: one display per target, every source a target appears with is a candidate, a target is available if any of its
/// paths says so, connectors without a monitor are skipped, available displays come first (display-topology.md).
/// </summary>
internal static class CcdSnapshotBuilder
{
    public static List<AttachedDisplay> Build(CcdRawSnapshot raw, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(log);

        var names = raw.Targets.ToDictionary(t => (t.Adapter, t.TargetId));
        var adapters = raw.Adapters.ToDictionary(a => a.Adapter, StringComparer.Ordinal);
        var targets = new Dictionary<(string, uint), TargetAccumulator>();
        var order = new List<TargetAccumulator>();

        foreach (CcdRawPath path in raw.Paths)
        {
            if (!targets.TryGetValue((path.TargetAdapter, path.TargetId), out TargetAccumulator? target))
            {
                target = new TargetAccumulator(path.TargetAdapter, path.TargetId);
                targets.Add((path.TargetAdapter, path.TargetId), target);
                order.Add(target);
            }

            var source = new CcdSource(ParseAdapter(path.SourceAdapter), path.SourceId);
            if (!target.Sources.Contains(source))
            {
                target.Sources.Add(source);
            }

            target.Available |= path.TargetAvailable;
            if (path.Active)
            {
                target.ActivePath = path;
            }
        }

        var warnedAdapters = new HashSet<string>(StringComparer.Ordinal);
        var displays = new List<AttachedDisplay>();
        foreach (TargetAccumulator target in order)
        {
            if (!names.TryGetValue((target.Adapter, target.TargetId), out CcdRawTarget? name) || name.NameError != 0)
            {
                log.Debug("Target {Adapter}:{TargetId} has no device name (error {Error}), skipped", target.Adapter, target.TargetId, name?.NameError);
                continue;
            }

            if (name.MonitorDevicePath.Length == 0)
            {
                // Connector without a known monitor – nothing a profile could refer to.
                continue;
            }

            string adapterPath = adapters.TryGetValue(target.Adapter, out CcdRawAdapter? adapter) && adapter.Error == 0 ? adapter.DevicePath : string.Empty;
            if (adapterPath.Length == 0 && warnedAdapters.Add(target.Adapter))
            {
                log.Warning("Adapter {Adapter} has no device path (error {Error})", target.Adapter, adapter?.Error);
            }

            var identity = new DisplayIdentity
            {
                AdapterDevicePath = adapterPath,
                TargetDevicePath = name.MonitorDevicePath,
                EdidManufacturerId = name.EdidIdsValid ? name.EdidManufacturerId : (ushort)0,
                EdidProductCodeId = name.EdidIdsValid ? name.EdidProductCodeId : (ushort)0,
                FriendlyName = name.FriendlyName,
            };

            CcdSource? activeSource = target.ActivePath is { } active
                ? new CcdSource(ParseAdapter(active.SourceAdapter), active.SourceId)
                : null;

            displays.Add(new AttachedDisplay
            {
                Identity = identity,
                IsAvailable = target.Available,
                IsActive = target.ActivePath is not null,
                ActiveMode = target.ActivePath is { } activePath && DecodeActiveMode(identity, activePath, raw.Modes) is { } mode
                    ? mode with { Hdr = name.Hdr }
                    : null,
                NativeHandle = new CcdTargetHandle(ParseAdapter(target.Adapter), target.TargetId, target.Sources, activeSource),
            });
        }

        // Available targets first: if a monitor shows up on two targets, the planner takes the usable one.
        return displays.OrderByDescending(d => d.IsAvailable).ThenByDescending(d => d.IsActive).ToList();
    }

    /// <summary>Inverse of <see cref="AdapterLuid.ToString"/>: high part, then low part, 8 hex digits each.</summary>
    internal static AdapterLuid ParseAdapter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length != 16)
        {
            throw new FormatException($"Adapter LUID '{text}' must have 16 hex digits.");
        }

        return new AdapterLuid(
            uint.Parse(text.AsSpan(8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
            unchecked((int)uint.Parse(text.AsSpan(0, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)));
    }

    private static DisplayAssignment? DecodeActiveMode(DisplayIdentity identity, CcdRawPath path, IReadOnlyList<CcdRawMode> modes)
    {
        if (path.SourceModeIndex >= modes.Count || modes[(int)path.SourceModeIndex].Kind != CcdModeKind.Source)
        {
            return null;
        }

        CcdRawMode source = modes[(int)path.SourceModeIndex];
        var refresh = new DISPLAYCONFIG_RATIONAL { Numerator = path.RefreshNumerator, Denominator = path.RefreshDenominator };
        if (path.TargetModeIndex < modes.Count && modes[(int)path.TargetModeIndex] is { Kind: CcdModeKind.Target } target)
        {
            refresh = new DISPLAYCONFIG_RATIONAL { Numerator = target.VSyncNumerator, Denominator = target.VSyncDenominator };
        }

        var sourceMode = new DISPLAYCONFIG_SOURCE_MODE
        {
            width = source.Width,
            height = source.Height,
            position = new POINTL { x = source.PositionX, y = source.PositionY },
        };
        return CcdModes.ToAssignment(identity, sourceMode, refresh, (DISPLAYCONFIG_ROTATION)path.Rotation);
    }

    private sealed class TargetAccumulator(string adapter, uint targetId)
    {
        public string Adapter { get; } = adapter;

        public uint TargetId { get; } = targetId;

        public List<CcdSource> Sources { get; } = [];

        public bool Available { get; set; }

        public CcdRawPath? ActivePath { get; set; }
    }
}
