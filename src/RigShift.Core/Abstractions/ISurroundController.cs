using RigShift.Core.Profiles;

namespace RigShift.Core.Abstractions;

/// <summary>
/// OS boundary for NVIDIA Surround (Mosaic). Everything here is defensive on purpose: the graphics driver is the one
/// part of a switch that can fail in a way Windows cannot undo, and a driver bug in this area is on record (RTX 50
/// series, 2026). Reading the state never changes anything, and an unclear state is never written over.
/// </summary>
public interface ISurroundController
{
    /// <summary>Reads the Surround state. Answers <see cref="SurroundAvailability.NoDriver"/> on machines without NVAPI.</summary>
    Task<SurroundState> QueryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Builds or removes Surround grids. Does nothing and reports <see cref="SurroundOutcome.Unchanged"/> when the
    /// wanted state already runs – a rebuild costs seconds of black screen for no gain.
    /// </summary>
    Task<SurroundApplyResult> ApplyAsync(SurroundSetting wanted, CancellationToken cancellationToken);

    /// <summary>
    /// Display ids the driver currently offers, with the names it knows, so a profile can be built without guessing
    /// ids. Empty when Surround is unavailable.
    /// </summary>
    Task<IReadOnlyList<SurroundDisplay>> ListDisplaysAsync(CancellationToken cancellationToken);
}

/// <summary>The Surround state at one moment.</summary>
public sealed record SurroundState
{
    public required SurroundAvailability Availability { get; init; }

    /// <summary>Grids the driver reports; empty while Surround is off.</summary>
    public IReadOnlyList<SurroundGrid> Grids { get; init; } = [];

    /// <summary>What the driver answered, including its status code, when the state could not be read.</summary>
    public string? Message { get; init; }

    public bool IsActive => Grids.Count > 0;

    public static SurroundState Unavailable(SurroundAvailability availability, string? message = null) =>
        new() { Availability = availability, Message = message };
}

public enum SurroundAvailability
{
    /// <summary>No NVAPI on this machine: no NVIDIA driver, or a 32-bit-only one. Surround is simply not a topic.</summary>
    NoDriver,

    /// <summary>NVAPI is there but would not answer. Nothing is touched in this state.</summary>
    Unknown,

    Available,
}

/// <summary>Result of a Surround change. The message always names what the driver answered (plan point 21).</summary>
public sealed record SurroundApplyResult
{
    public required SurroundOutcome Outcome { get; init; }

    public string? Message { get; init; }

    public static readonly SurroundApplyResult NotConfigured = new() { Outcome = SurroundOutcome.NotConfigured };

    public static readonly SurroundApplyResult Unchanged = new() { Outcome = SurroundOutcome.Unchanged };
}

public enum SurroundOutcome
{
    /// <summary>The profile says nothing about Surround.</summary>
    NotConfigured,

    /// <summary>The wanted state already ran.</summary>
    Unchanged,

    Changed,

    /// <summary>No NVIDIA driver, or its state was unreadable. The switch goes on without touching Surround.</summary>
    NotAvailable,

    Failed,
}
