using RigShift.Core.Profiles;
using Windows.Win32;
using Windows.Win32.Devices.Display;

namespace RigShift.Windows.Display;

/// <summary>
/// Builds the complete path and mode arrays for ONE <c>SetDisplayConfig</c> call (display-topology.md, rule 1).
/// Pure function over the resolved targets, so the struct wiring is unit-testable without monitors.
/// </summary>
internal static class CcdPathBuilder
{
    public static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) Build(
        IReadOnlyList<(CcdTargetHandle Target, DisplayAssignment Assignment)> displays, bool databaseModes, bool allowClone = false)
    {
        ArgumentNullException.ThrowIfNull(displays);

        CcdSource[] sources = AssignSources(displays, allowClone);
        var sourceModes = new Dictionary<CcdSource, uint>();
        var paths = new DISPLAYCONFIG_PATH_INFO[displays.Count];
        var modes = new List<DISPLAYCONFIG_MODE_INFO>();

        for (int i = 0; i < displays.Count; i++)
        {
            (CcdTargetHandle target, DisplayAssignment assignment) = displays[i];
            CcdSource source = sources[i];

            var path = new DISPLAYCONFIG_PATH_INFO { flags = PInvoke.DISPLAYCONFIG_PATH_ACTIVE };
            path.sourceInfo.adapterId = source.Adapter.ToLuid();
            path.sourceInfo.id = source.Id;
            path.targetInfo.adapterId = target.Adapter.ToLuid();
            path.targetInfo.id = target.TargetId;
            path.targetInfo.rotation = (DISPLAYCONFIG_ROTATION)(int)assignment.Rotation;
            path.targetInfo.scaling = DISPLAYCONFIG_SCALING.DISPLAYCONFIG_SCALING_PREFERRED;

            bool hasRefresh = assignment.RefreshNumerator != 0 && assignment.RefreshDenominator != 0;
            path.targetInfo.refreshRate = hasRefresh
                ? new DISPLAYCONFIG_RATIONAL { Numerator = assignment.RefreshNumerator, Denominator = assignment.RefreshDenominator }
                : default;
            // A 0/0 refresh rate requires UNSPECIFIED, otherwise SetDisplayConfig fails.
            path.targetInfo.scanLineOrdering = hasRefresh
                ? DISPLAYCONFIG_SCANLINE_ORDERING.DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE
                : DISPLAYCONFIG_SCANLINE_ORDERING.DISPLAYCONFIG_SCANLINE_ORDERING_UNSPECIFIED;

            // No target mode: the OS picks the timing for the requested resolution and refresh rate.
            path.targetInfo.modeInfoIdx = PInvoke.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;

            if (databaseModes)
            {
                // Fallback (rule 5): no modes at all, Windows takes resolution and position from its database.
                path.sourceInfo.modeInfoIdx = PInvoke.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            }
            else if (sourceModes.TryGetValue(source, out uint shared))
            {
                // Duplicated displays: one source, so one source mode for both paths.
                path.sourceInfo.modeInfoIdx = shared;
            }
            else
            {
                var mode = new DISPLAYCONFIG_MODE_INFO
                {
                    infoType = DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
                    id = source.Id,
                    adapterId = source.Adapter.ToLuid(),
                };
                mode.sourceMode.width = (uint)assignment.Width;
                mode.sourceMode.height = (uint)assignment.Height;
                mode.sourceMode.pixelFormat = DISPLAYCONFIG_PIXELFORMAT.DISPLAYCONFIG_PIXELFORMAT_32BPP;
                mode.sourceMode.position.x = assignment.PositionX;
                mode.sourceMode.position.y = assignment.PositionY;

                path.sourceInfo.modeInfoIdx = (uint)modes.Count;
                sourceModes[source] = (uint)modes.Count;
                modes.Add(mode);
            }

            paths[i] = path;
        }

        return (paths, modes.ToArray());
    }

    /// <summary>
    /// Every active path needs its own source, otherwise Windows clones the desktop. Targets keep their current
    /// source where possible; the rest take the lowest free one they support. With <paramref name="allowClone"/> a
    /// display on exactly the rectangle of an earlier one takes that one's source instead – that is what duplicating is.
    /// </summary>
    private static CcdSource[] AssignSources(IReadOnlyList<(CcdTargetHandle Target, DisplayAssignment Assignment)> displays, bool allowClone)
    {
        var chosen = new CcdSource?[displays.Count];
        var used = new HashSet<CcdSource>();
        int[] cloneOf = [.. Enumerable.Range(0, displays.Count).Select(i => allowClone ? FirstOnSameRectangle(displays, i) : i)];

        for (int i = 0; i < displays.Count; i++)
        {
            if (cloneOf[i] == i && displays[i].Target.ActiveSource is { } active && used.Add(active))
            {
                chosen[i] = active;
            }
        }

        for (int i = 0; i < displays.Count; i++)
        {
            if (chosen[i] is not null || cloneOf[i] != i)
            {
                continue;
            }

            CcdTargetHandle target = displays[i].Target;
            foreach (CcdSource candidate in target.Sources.OrderBy(s => s.Id))
            {
                if (used.Add(candidate))
                {
                    chosen[i] = candidate;
                    break;
                }
            }

            if (chosen[i] is null)
            {
                throw new InvalidOperationException(
                    $"No free display source for target {target} ({DisplayNames.Of(displays[i].Assignment)}).");
            }
        }

        for (int i = 0; i < displays.Count; i++)
        {
            // The first display of a rectangle has its source by now; a target that cannot show it gets its own after all.
            if (cloneOf[i] != i)
            {
                CcdSource original = chosen[cloneOf[i]]!.Value;
                chosen[i] = displays[i].Target.Sources.Contains(original)
                    ? original
                    : displays[i].Target.Sources.OrderBy(s => s.Id).Cast<CcdSource?>().FirstOrDefault(s => used.Add(s!.Value))
                        ?? throw new InvalidOperationException(
                            $"No free display source for target {displays[i].Target} ({DisplayNames.Of(displays[i].Assignment)}).");
            }
        }

        return chosen.Select(c => c!.Value).ToArray();
    }

    /// <summary>The index of the first display with this one's position and size – its own index when it is the first.</summary>
    private static int FirstOnSameRectangle(IReadOnlyList<(CcdTargetHandle Target, DisplayAssignment Assignment)> displays, int index)
    {
        DisplayAssignment wanted = displays[index].Assignment;
        for (int i = 0; i < index; i++)
        {
            DisplayAssignment other = displays[i].Assignment;
            if (other.PositionX == wanted.PositionX && other.PositionY == wanted.PositionY
                && other.Width == wanted.Width && other.Height == wanted.Height)
            {
                return i;
            }
        }

        return index;
    }
}
