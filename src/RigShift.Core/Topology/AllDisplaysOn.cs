using System.ComponentModel;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// The emergency way back to a picture: every display Windows reports as ready, in ONE call, with the resolution and
/// position Windows picks. When the card cannot drive them all at their current refresh rates – NVIDIA's head budget,
/// rule 1 of docs/display-topology.md – the same call asks for 60 Hz, which needs no stream compression. Nothing is saved
/// to the Windows display database: the next profile switch replaces this layout anyway.
/// </summary>
internal sealed class AllDisplaysOn(IDisplayConfigurator display, ILogger log)
{
    private static readonly ApplyOptions Options = new() { UseDatabaseModes = true, SaveToDatabase = false };

    public async Task<AllDisplaysOnResult> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunCoreAsync(cancellationToken);
        }
        catch (Win32Exception ex)
        {
            log.Warning(ex, "All displays on failed with error {Error}", ex.NativeErrorCode);
            return new AllDisplaysOnResult(AllDisplaysOnOutcome.Failed, 0, ex.NativeErrorCode);
        }
    }

    private async Task<AllDisplaysOnResult> RunCoreAsync(CancellationToken cancellationToken)
    {
        DisplaySnapshot snapshot = await display.QueryAsync(cancellationToken);

        // The active ones first: the order of the paths is their priority.
        List<AttachedDisplay> ready = [.. snapshot.Displays.Where(d => d.IsActive || d.IsAvailable).OrderByDescending(d => d.IsActive)];
        if (ready.Count == 0)
        {
            log.Warning("All displays on: Windows reports no display that is ready");
            return new AllDisplaysOnResult(AllDisplaysOnOutcome.NoDisplays, 0, 0);
        }

        if (ready.All(d => d.IsActive))
        {
            log.Information("All displays on: all {Count} displays are on already", ready.Count);
            return new AllDisplaysOnResult(AllDisplaysOnOutcome.AlreadyOn, ready.Count, 0);
        }

        log.Information("All displays on: turning on {Off} of {Count} displays ({Names})",
            ready.Count(d => !d.IsActive), ready.Count, string.Join(", ", ready.Select(d => DisplayNames.Of(d.Identity))));
        int error = await display.ApplyAsync(PlanFor(ready, sixtyHertz: false), Options, cancellationToken);
        if (error == 0)
        {
            return new AllDisplaysOnResult(AllDisplaysOnOutcome.TurnedOn, ready.Count, 0);
        }

        log.Warning("All displays on failed with error {Error}; trying again at 60 Hz", error);
        error = await display.ApplyAsync(PlanFor(ready, sixtyHertz: true), Options, cancellationToken);
        if (error == 0)
        {
            return new AllDisplaysOnResult(AllDisplaysOnOutcome.TurnedOnAt60Hz, ready.Count, 0);
        }

        log.Warning("All displays on at 60 Hz failed with error {Error}", error);
        return new AllDisplaysOnResult(AllDisplaysOnOutcome.Failed, ready.Count, error);
    }

    private static TopologyPlan PlanFor(IReadOnlyList<AttachedDisplay> displays, bool sixtyHertz)
    {
        List<PlannedDisplay> planned = [.. displays.Select(d => new PlannedDisplay(AssignmentFor(d, sixtyHertz), d))];
        return new TopologyPlan
        {
            Profile = new Profile { Id = Guid.Empty, Name = "All displays on", Displays = [.. planned.Select(p => p.Assignment)] },
            Resolved = planned,
            Missing = [],
            Warnings = [],
        };
    }

    /// <summary>
    /// With the modes left to Windows only rotation and refresh rate reach the call: an active display keeps both, one
    /// that is off gets what Windows prefers.
    /// </summary>
    private static DisplayAssignment AssignmentFor(AttachedDisplay display, bool sixtyHertz)
    {
        DisplayAssignment assignment = display.ActiveMode ?? new DisplayAssignment
        {
            Identity = display.Identity,
            Width = 0,
            Height = 0,
            RefreshNumerator = 0,
            RefreshDenominator = 0,
            PositionX = 0,
            PositionY = 0,
        };
        return sixtyHertz ? assignment with { RefreshNumerator = 60, RefreshDenominator = 1 } : assignment;
    }
}

public enum AllDisplaysOnOutcome
{
    TurnedOn,

    /// <summary>On, but at 60 Hz: the card could not drive all displays at their refresh rates.</summary>
    TurnedOnAt60Hz,

    /// <summary>Every display Windows reports as ready was on already.</summary>
    AlreadyOn,

    NoDisplays,
    Failed,
}

/// <param name="Displays">How many displays were ready, i.e. are on now if it worked.</param>
/// <param name="Error">The native error of the last attempt when it failed, otherwise 0.</param>
public sealed record AllDisplaysOnResult(AllDisplaysOnOutcome Outcome, int Displays, int Error);
