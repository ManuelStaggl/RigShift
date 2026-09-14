namespace RigShift.Core.Topology;

/// <summary>
/// Numbers for the displays page and "Identify". Windows offers no API for the numbers of its settings page; they follow
/// the GDI names <c>\\.\DISPLAYn</c> in most setups, so those are used (finding HW-04). Without them for every active display
/// the numbers go left to right, so no two displays share a number by accident.
/// </summary>
public static class DisplayNumbers
{
    /// <summary>Active displays first, by number; inactive ones get none.</summary>
    public static IReadOnlyList<(AttachedDisplay Display, int? Number)> Assign(IEnumerable<AttachedDisplay> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);

        List<AttachedDisplay> active = displays.Where(d => d.IsActive && d.ActiveMode is not null).ToList();
        List<AttachedDisplay> inactive = displays.Where(d => !(d.IsActive && d.ActiveMode is not null)).ToList();
        var numbered = new List<(AttachedDisplay, int?)>();

        if (active.Count > 0 && active.All(d => d.WindowsNumber is not null))
        {
            numbered.AddRange(active
                .OrderBy(d => d.WindowsNumber)
                .ThenBy(d => d.ActiveMode!.PositionX)
                .Select(d => (d, d.WindowsNumber)));
        }
        else
        {
            int number = 0;
            numbered.AddRange(active
                .OrderBy(d => d.ActiveMode!.PositionX)
                .ThenBy(d => d.ActiveMode!.PositionY)
                .Select(d => (d, (int?)++number)));
        }

        numbered.AddRange(inactive.Select(d => (d, (int?)null)));
        return numbered;
    }
}
