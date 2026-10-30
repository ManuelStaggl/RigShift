namespace RigShift.App.Controls;

public enum InfoKind
{
    Info,
    Warn,
    Error,
}

/// <summary>Color of a status: always paired with text, never the only signal.</summary>
public enum StatusKind
{
    Neutral,
    Ok,
    Warn,
    Error,

    /// <summary>Something is in progress (a switch, a running game).</summary>
    Accent,

    /// <summary>The profile on screen now: a check mark in the accent, the same everywhere.</summary>
    Active,
}
