using System.Globalization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.Windows.Display;

/// <summary>
/// NVIDIA Surround through NVAPI's Mosaic calls. Careful by design: the state is read first, a grid that already runs
/// is left alone (rebuilding it costs seconds of black screen), a grid is validated before it is set, and a change is
/// checked afterwards - a driver bug on record answers "done" to a call that did nothing.
/// </summary>
public sealed class NvSurroundController : ISurroundController, IDisposable
{
    private readonly IDisplayConfigurator _display;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private NvApi? _api;
    private string? _loadFailure;
    private bool _loaded;
    private bool _disposed;

    public NvSurroundController(IDisplayConfigurator display, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(log);

        _display = display;
        _log = log.ForContext<NvSurroundController>();
    }

    public Task<SurroundState> QueryAsync(CancellationToken cancellationToken) =>
        Task.Run(Read, cancellationToken);

    public Task<SurroundApplyResult> ApplyAsync(SurroundSetting wanted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        return Task.Run(() => Apply(wanted), cancellationToken);
    }

    public async Task<IReadOnlyList<SurroundDisplay>> ListDisplaysAsync(CancellationToken cancellationToken)
    {
        List<uint> ids = await Task.Run(ListDisplayIds, cancellationToken);
        if (ids.Count == 0)
        {
            return [];
        }

        Dictionary<uint, string> names = await NamesAsync(ids, cancellationToken);
        return [.. ids.Select(id => new SurroundDisplay { DisplayId = id, Name = names.GetValueOrDefault(id) })];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _api?.Dispose();
            _api = null;
        }
    }

    /// <summary>Opens NVAPI once. A machine without an NVIDIA driver answers null forever, without trying again.</summary>
    private NvApi? Api()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loaded)
            {
                return _api;
            }

            _loaded = true;
            try
            {
                _api = NvApi.TryOpen(out _loadFailure);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                _loadFailure = ex.Message;
                _api = null;
            }

            if (_api is null)
            {
                _log.Information("NVIDIA Surround is not available here: {Reason}", _loadFailure ?? "no nvapi64.dll");
            }
            else
            {
                _log.Debug("NVAPI loaded for Surround");
            }

            return _api;
        }
    }

    private SurroundState Read()
    {
        if (Api() is not { } api)
        {
            return SurroundState.Unavailable(SurroundAvailability.NoDriver, _loadFailure);
        }

        lock (_gate)
        {
            // Api() let go of the lock: a Dispose in between has unloaded the library this would call into.
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadLocked(api);
        }
    }

    private SurroundState ReadLocked(NvApi api)
    {
        MosaicGridTopoV2[] grids = new MosaicGridTopoV2[MaxGrids];
        int status = api.EnumDisplayGrids(grids, out uint count);
        if (status != NvApi.Status.Ok)
        {
            string message = api.Describe(status);
            _log.Warning("Surround state could not be read: {Message}", message);
            return SurroundState.Unavailable(SurroundAvailability.Unknown, message);
        }

        // A display outside every Surround grid is reported as its own 1x1 grid; only the bigger ones are Surround.
        List<SurroundGrid> surround = [];
        for (int i = 0; i < count; i++)
        {
            if (grids[i].DisplayCount > 1)
            {
                surround.Add(ToGrid(grids[i]));
            }
        }

        return new SurroundState { Availability = SurroundAvailability.Available, Grids = surround };
    }

    private SurroundApplyResult Apply(SurroundSetting wanted)
    {
        if (Api() is not { } api)
        {
            return new SurroundApplyResult
            {
                Outcome = SurroundOutcome.NotAvailable,
                Message = _loadFailure ?? "No NVIDIA graphics driver was found, so Surround cannot be set here.",
            };
        }

        lock (_gate)
        {
            // Api() let go of the lock: a Dispose in between has unloaded the library this would call into.
            ObjectDisposedException.ThrowIf(_disposed, this);
            SurroundState before = ReadLocked(api);
            if (before.Availability != SurroundAvailability.Available)
            {
                // An unclear state is never written over (plan point 21).
                return new SurroundApplyResult { Outcome = SurroundOutcome.NotAvailable, Message = before.Message };
            }

            return wanted.Enabled ? Enable(api, wanted.Grid, before) : Disable(api, before);
        }
    }

    private SurroundApplyResult Enable(NvApi api, SurroundGrid? grid, SurroundState before)
    {
        if (grid is null || !grid.IsComplete)
        {
            return new SurroundApplyResult
            {
                Outcome = SurroundOutcome.Failed,
                Message = "The profile wants Surround but does not say which displays form the grid.",
            };
        }

        if (before.Grids.Any(active => Matches(active, grid)))
        {
            _log.Information("Surround already runs the wanted grid ({Columns}x{Rows}), leaving it alone", grid.Columns, grid.Rows);
            return SurroundApplyResult.Unchanged;
        }

        MosaicGridTopoV2[] topo = [ToTopo(grid)];
        return Refusal(api, topo, switchingOn: true) ?? Set(api, topo, "switch Surround on", before);
    }

    private SurroundApplyResult Disable(NvApi api, SurroundState before)
    {
        if (!before.IsActive)
        {
            return SurroundApplyResult.Unchanged;
        }

        MosaicGridTopoV2[] singles = [.. Singles(before.Grids).Select(ToTopo)];
        return Refusal(api, singles, switchingOn: false) ?? Set(api, singles, "switch Surround off", before);
    }

    /// <summary>
    /// Surround goes away by giving every display of every grid its own 1x1 grid again: in the mode and rotation it runs
    /// now, without overlap. A mode of 0x0 at 0 Hz, as before 4.0, is nowhere documented to mean "the driver's choice".
    /// </summary>
    internal static IEnumerable<SurroundGrid> Singles(IEnumerable<SurroundGrid> grids) =>
        grids.SelectMany(grid => grid.Displays.Select(display => new SurroundGrid
        {
            Rows = 1,
            Columns = 1,
            Width = grid.Width,
            Height = grid.Height,
            RefreshRateHz = grid.RefreshRateHz,
            BezelCorrected = false,
            Displays = [display with { OverlapX = 0, OverlapY = 0 }],
        }));

    /// <summary>
    /// Lets the driver check the grids before they are set. Returns why it refuses them, or <c>null</c> when they can be
    /// set - a grid that needs a driver reload counts as refused: the reload closes running games (plan point 21).
    /// </summary>
    private SurroundApplyResult? Refusal(NvApi api, MosaicGridTopoV2[] grids, bool switchingOn)
    {
        var verdicts = new MosaicDisplayTopoStatus[grids.Length];
        int status = api.ValidateDisplayGrids(grids, verdicts);
        if (status != NvApi.Status.Ok)
        {
            return Failure(api, "The graphics driver refused to check the Surround grid", status);
        }

        foreach (MosaicDisplayTopoStatus check in verdicts)
        {
            string? problems = DisplayProblems(check);
            if (check.ErrorFlags != 0 || problems is not null)
            {
                string detail = MosaicProblems.Describe(check.ErrorFlags) ?? problems ?? "no reason given";
                _log.Warning("Surround grid rejected by the driver: {Detail}", detail);
                return new SurroundApplyResult
                {
                    Outcome = SurroundOutcome.Failed,
                    Message = (switchingOn ? "The graphics driver cannot build this Surround grid: " : "The graphics driver cannot switch Surround off: ")
                        + detail + ".",
                };
            }

            if ((check.WarningFlags & MosaicDisplayTopoStatus.WarningDriverReloadRequired) != 0)
            {
                return new SurroundApplyResult
                {
                    Outcome = SurroundOutcome.Failed,
                    Message = switchingOn
                        ? "Surround would only start if the graphics driver reloaded itself, which would close running games. "
                            + "Switch Surround on once in the NVIDIA control panel, then this profile can do it without a reload."
                        : "Surround would only stop if the graphics driver reloaded itself, which would close running games. "
                            + "Switch Surround off once in the NVIDIA control panel, then this profile can do it without a reload.",
                };
            }
        }

        return null;
    }

    private SurroundApplyResult Set(NvApi api, MosaicGridTopoV2[] grids, string what, SurroundState before)
    {
        _log.Information("Asking the graphics driver to {What} ({Grids} grid(s))", what, grids.Length);
        int status = api.SetDisplayGrids(grids);
        if (status != NvApi.Status.Ok)
        {
            return Failure(api, "The graphics driver could not " + what, status);
        }

        // The driver is on record answering "done" without doing anything (RTX 50 series, 2026). Look, do not trust.
        SurroundState after = ReadLocked(api);
        if (after.Availability == SurroundAvailability.Available && SameGrids(before, after))
        {
            _log.Error("The driver reported success but Surround did not change");
            return new SurroundApplyResult
            {
                Outcome = SurroundOutcome.Failed,
                Message = "The graphics driver reported success, but Surround did not change. "
                    + "This is a known driver fault; switching Surround once in the NVIDIA control panel usually clears it.",
            };
        }

        _log.Information("Surround changed: {Grids} grid(s) active", after.Grids.Count);
        return new SurroundApplyResult { Outcome = SurroundOutcome.Changed };
    }

    private SurroundApplyResult Failure(NvApi api, string what, int status)
    {
        string message = api.Describe(status);
        _log.Error("{What}: {Message}", what, message);
        return new SurroundApplyResult { Outcome = SurroundOutcome.Failed, Message = what + ": " + message + "." };
    }

    /// <summary>The first display of the check that carries a problem flag, in words.</summary>
    private static string? DisplayProblems(MosaicDisplayTopoStatus check)
    {
        for (int i = 0; i < check.DisplayCount && i < NvApi.MaxDisplays; i++)
        {
            if (MosaicProblems.Describe(check.Displays[i].ErrorFlags) is { } text)
            {
                return string.Create(CultureInfo.InvariantCulture, $"display {check.Displays[i].DisplayId:X8}: {text}");
            }
        }

        return null;
    }

    private List<uint> ListDisplayIds()
    {
        if (Api() is not { } api)
        {
            return [];
        }

        lock (_gate)
        {
            // Api() let go of the lock: a Dispose in between has unloaded the library this would call into.
            ObjectDisposedException.ThrowIf(_disposed, this);
            MosaicGridTopoV2[] grids = new MosaicGridTopoV2[MaxGrids];
            if (api.EnumDisplayGrids(grids, out uint count) != NvApi.Status.Ok)
            {
                return [];
            }

            List<uint> ids = [];
            for (int i = 0; i < count; i++)
            {
                for (int cell = 0; cell < grids[i].DisplayCount && cell < NvApi.MaxMosaicDisplays; cell++)
                {
                    ids.Add(grids[i].Displays[cell].DisplayId);
                }
            }

            return ids;
        }
    }

    /// <summary>
    /// Monitor names for driver display ids, by asking the driver which Windows target each id sits on and looking that
    /// target up in the live display list. Ids the driver or Windows will not place simply get no name.
    /// </summary>
    private async Task<Dictionary<uint, string>> NamesAsync(IReadOnlyList<uint> ids, CancellationToken cancellationToken)
    {
        var names = new Dictionary<uint, string>();
        if (Api() is not { } api)
        {
            return names;
        }

        DisplaySnapshot snapshot;
        try
        {
            snapshot = await _display.QueryAsync(cancellationToken);
        }
        catch (Exception ex) when (DisplayApiFailure.Is(ex))
        {
            _log.Debug(ex, "Display names for Surround could not be read");
            return names;
        }

        foreach (uint id in ids)
        {
            bool placed;
            uint low = 0;
            int high = 0;
            uint target = 0;
            lock (_gate)
            {
                placed = !_disposed && api.TryGetDisplayTarget(id, out low, out high, out target);
            }

            if (!placed)
            {
                continue;
            }

            AttachedDisplay? match = snapshot.Displays.FirstOrDefault(d =>
                d.NativeHandle is CcdTargetHandle handle
                && handle.TargetId == target
                && handle.Adapter.LowPart == low
                && handle.Adapter.HighPart == high);
            if (match is not null && !string.IsNullOrWhiteSpace(match.Identity.FriendlyName))
            {
                names[id] = match.Identity.FriendlyName;
            }
        }

        return names;
    }

    private static bool SameGrids(SurroundState left, SurroundState right) =>
        left.Grids.Count == right.Grids.Count
        && left.Grids.Zip(right.Grids).All(pair => Matches(pair.First, pair.Second));

    /// <summary>
    /// Whether a running grid is the one a profile asks for. A refresh rate of 0 means "whatever the driver picked"; a
    /// grid saved before 4.0 knows no bezel correction or rotation and accepts whatever runs, as it always did.
    /// </summary>
    internal static bool Matches(SurroundGrid active, SurroundGrid wanted) =>
        active.Rows == wanted.Rows
        && active.Columns == wanted.Columns
        && active.Width == wanted.Width
        && active.Height == wanted.Height
        && (wanted.RefreshRateHz == 0 || active.RefreshRateHz == wanted.RefreshRateHz)
        && (wanted.HasLayout
            ? active.Displays.Select(Layout).SequenceEqual(wanted.Displays.Select(Layout))
            : active.Displays.Select(d => d.DisplayId).SequenceEqual(wanted.Displays.Select(d => d.DisplayId)));

    private static (uint Id, int OverlapX, int OverlapY, DisplayRotation Rotation) Layout(SurroundDisplay display) =>
        (display.DisplayId, display.OverlapX, display.OverlapY, display.Rotation);

    internal static SurroundGrid ToGrid(in MosaicGridTopoV2 topo)
    {
        List<SurroundDisplay> displays = [];
        for (int cell = 0; cell < topo.DisplayCount && cell < NvApi.MaxMosaicDisplays; cell++)
        {
            MosaicGridTopoDisplayV2 display = topo.Displays[cell];
            displays.Add(new SurroundDisplay
            {
                DisplayId = display.DisplayId,
                OverlapX = display.OverlapX,
                OverlapY = display.OverlapY,
                Rotation = FromNvRotation(display.Rotation),
            });
        }

        return new SurroundGrid
        {
            Rows = (int)topo.Rows,
            Columns = (int)topo.Columns,
            Width = (int)topo.DisplaySettings.Width,
            Height = (int)topo.DisplaySettings.Height,
            RefreshRateHz = (int)topo.DisplaySettings.Frequency,
            BezelCorrected = (topo.Flags & MosaicGridTopoV2.FlagApplyWithBezelCorrect) != 0,
            Displays = displays,
        };
    }

    internal static MosaicGridTopoV2 ToTopo(SurroundGrid grid)
    {
        MosaicGridTopoV2 topo = default;
        topo.Version = MosaicGridTopoV2.StructVersion;
        topo.Rows = (uint)grid.Rows;
        topo.Columns = (uint)grid.Columns;
        topo.DisplayCount = (uint)grid.Displays.Count;

        // Overlaps alone build the grid at the uncorrected resolution. They also count in case a driver leaves the flag
        // out when it reports a running grid - losing the correction on every rebuild is what 4.0 fixes.
        bool corrected = grid.BezelCorrected == true || grid.Displays.Any(d => d.OverlapX != 0 || d.OverlapY != 0);
        topo.Flags = corrected ? MosaicGridTopoV2.FlagApplyWithBezelCorrect : 0;
        for (int cell = 0; cell < grid.Displays.Count && cell < NvApi.MaxMosaicDisplays; cell++)
        {
            SurroundDisplay display = grid.Displays[cell];
            topo.Displays[cell].Version = MosaicGridTopoDisplayV2.Version2;
            topo.Displays[cell].DisplayId = display.DisplayId;
            topo.Displays[cell].OverlapX = display.OverlapX;
            topo.Displays[cell].OverlapY = display.OverlapY;
            topo.Displays[cell].Rotation = ToNvRotation(display.Rotation);
        }

        topo.DisplaySettings.Version = MosaicDisplaySettingV1.Version1;
        topo.DisplaySettings.Width = (uint)grid.Width;
        topo.DisplaySettings.Height = (uint)grid.Height;
        topo.DisplaySettings.BitsPerPixel = 32;
        topo.DisplaySettings.Frequency = (uint)grid.RefreshRateHz;
        return topo;
    }

    /// <summary>NV_ROTATE: 0, 90, 180 and 270 degrees as 0 to 3; 4 ("ignored") reads as none.</summary>
    private static DisplayRotation FromNvRotation(uint rotation) => rotation switch
    {
        1 => DisplayRotation.Rotate90,
        2 => DisplayRotation.Rotate180,
        3 => DisplayRotation.Rotate270,
        _ => DisplayRotation.Identity,
    };

    private static uint ToNvRotation(DisplayRotation rotation) => rotation switch
    {
        DisplayRotation.Rotate90 => 1,
        DisplayRotation.Rotate180 => 2,
        DisplayRotation.Rotate270 => 3,
        _ => 0,
    };

    /// <summary>Enough for every display the driver can drive, each as its own grid.</summary>
    private const int MaxGrids = NvApi.MaxDisplays;
}
