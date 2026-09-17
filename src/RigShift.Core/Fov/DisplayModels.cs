namespace RigShift.Core.Fov;

/// <summary>
/// The curvature of a panel is in no EDID and in no DisplayID block, so it has to come from the model name. The table
/// holds the monitors sim racers actually use; everything else starts flat and the user picks the radius once.
/// </summary>
public static class DisplayModels
{
    /// <summary>Longest name first, so <c>G95SC</c> is not eaten by <c>G95C</c>.</summary>
    private static readonly (string Name, int RadiusMm)[] Known =
    [
        ("45GR95QE", 800),
        ("45GS95QE", 800),
        ("AW3423DW", 1800),
        ("AW3423DWF", 1800),
        ("C49G95T", 1000),
        ("LC49G95T", 1000),
        ("C32G75T", 1000),
        ("LC32G75T", 1000),
        ("S49AG95", 1000),
        ("G95SC", 1800),
        ("G93SC", 1800),
        ("S49CG95", 1800),
        ("G95C", 1000),
        ("491CQP", 1800),
        ("341CQP", 1800),
        ("PG49WCD", 1800),
        ("XG49WCR", 1800),
        ("S3222DGM", 1800),
        ("ODYSSEY NEO G8", 1000),
        ("ODYSSEY G7", 1000),
    ];

    /// <summary>The radius in millimetres for a monitor name from the EDID, or <c>null</c> when the model is unknown.</summary>
    public static int? RadiusFor(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return null;
        }

        foreach ((string name, int radius) in Known.OrderByDescending(k => k.Name.Length))
        {
            if (friendlyName.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return radius;
            }
        }

        return null;
    }

    /// <summary>The radii the page offers as buttons; anything else is typed into the free field.</summary>
    public static IReadOnlyList<int> CommonRadii { get; } = [800, 1000, 1500, 1800, 2300];
}
