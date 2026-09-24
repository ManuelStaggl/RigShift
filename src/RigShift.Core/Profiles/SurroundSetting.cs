using System.Text.Json.Serialization;

namespace RigShift.Core.Profiles;

/// <summary>
/// What a profile wants from NVIDIA Surround (Mosaic): several physical displays driven as one wide display, or none.
/// A profile without this setting leaves Surround exactly as it is – that is the default, because turning Surround on
/// or off is a rebuild of the whole desktop and must never happen as a side effect.
/// </summary>
public sealed record SurroundSetting
{
    /// <summary>True builds <see cref="Grid"/>; false removes every Surround grid before the switch.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The grid to build. Required while <see cref="Enabled"/> is true, ignored otherwise.</summary>
    public SurroundGrid? Grid { get; init; }
}

/// <summary>
/// One Surround grid. Every display in it runs the same mode – that is a rule of the driver, not our choice.
/// </summary>
public sealed record SurroundGrid
{
    public required int Rows { get; init; }

    public required int Columns { get; init; }

    /// <summary>Width of a single display, not of the whole grid.</summary>
    public required int Width { get; init; }

    /// <summary>Height of a single display, not of the whole grid.</summary>
    public required int Height { get; init; }

    /// <summary>Whole hertz, the only resolution the driver's grid structure offers. 0 lets the driver choose.</summary>
    public int RefreshRateHz { get; init; }

    /// <summary>Displays in grid order, cell <c>row * <see cref="Columns"/> + column</c>.</summary>
    public required IReadOnlyList<SurroundDisplay> Displays { get; init; }

    /// <summary>
    /// Whether the grid runs at the bezel-corrected resolution, so the picture continues behind the frames between the
    /// displays (their overlaps say by how much). <c>null</c> in profiles saved before 4.0, which recorded neither the
    /// correction nor the rotation: a running grid is then taken as it is instead of rebuilt without them.
    /// </summary>
    public bool? BezelCorrected { get; init; }

    /// <summary>Whether the grid knows its bezel correction and rotation, i.e. was saved by 4.0 or later.</summary>
    [JsonIgnore]
    public bool HasLayout => BezelCorrected is not null;

    /// <summary>Grid width in pixels, ignoring bezel correction.</summary>
    [JsonIgnore]
    public int TotalWidth => Width * Columns;

    /// <summary>Grid height in pixels, ignoring bezel correction.</summary>
    [JsonIgnore]
    public int TotalHeight => Height * Rows;

    /// <summary>Whether the grid describes as many cells as it has displays.</summary>
    [JsonIgnore]
    public bool IsComplete => Rows > 0 && Columns > 0 && Displays.Count == Rows * Columns;
}

/// <summary>One display of a Surround grid.</summary>
public sealed record SurroundDisplay
{
    /// <summary>
    /// The graphics driver's display id. It belongs to a GPU output, not to a session, so it survives a reboot as long
    /// as the monitor stays on its port. It is the only key available here: while Surround runs, the physical monitors
    /// are gone from the Windows display list, so a profile saved then cannot name them any other way.
    /// </summary>
    public required uint DisplayId { get; init; }

    /// <summary>Monitor name when the profile was saved, for messages only – never used to find the display.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Bezel correction towards the neighbouring display as the driver reports it, in pixels: negative leaves a gap the
    /// picture continues behind (the frames), positive overlaps. Zero without correction.
    /// </summary>
    public int OverlapX { get; init; }

    /// <inheritdoc cref="OverlapX"/>
    public int OverlapY { get; init; }

    /// <summary>
    /// Rotation of this display inside the grid, e.g. a portrait triple. <c>set</c>: an <c>init</c> initializer is skipped
    /// when the key is missing.
    /// </summary>
    public DisplayRotation Rotation { get; set; } = DisplayRotation.Identity;
}
