namespace RigShift.Core.Profiles;

/// <summary>A refresh rate as the driver reports it, e.g. 239761/1000.</summary>
public readonly record struct RefreshRate(uint Numerator, uint Denominator)
{
    public double Hertz => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    /// <summary>Two rates the user could not tell apart in a list (same value to two decimals).</summary>
    public bool LooksLike(RefreshRate other) => Math.Round(Hertz, 2) == Math.Round(other.Hertz, 2);

    public static RefreshRate Of(DisplayAssignment display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return new RefreshRate(display.RefreshNumerator, display.RefreshDenominator);
    }
}
