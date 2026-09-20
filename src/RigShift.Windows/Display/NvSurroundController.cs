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

        MosaicGridTopoV2 topo = ToTopo(grid);
        int status = api.ValidateDisplayGrids(ref topo, out MosaicDisplayTopoStatus check);
        if (status != NvApi.Status.Ok)
        {
            return Failure(api, "The graphics driver refused to check the Surround grid", status);
        }

        string? problems = DisplayProblems(check);
        if (check.ErrorFlags != 0 || problems is not null)
        {
            string detail = MosaicProblems.Describe(check.ErrorFlags) ?? problems ?? "no reason given";
            _log.Warning("Surround grid rejected by the driver: {Detail}", detail);
            return new SurroundApplyResult
            {
                Outcome = SurroundOutcome.Failed,
                Message = "The graphics driver cannot build this Surround grid: " + detail + ".",
            };
        }

        if ((check.WarningFlags & MosaicDisplayTopoStatus.WarningDriverReloadRequired) != 0)
        {
            // Reloading the driver takes down every running GPU application; we never force it (plan point 21).
            return new SurroundApplyResult
            {
                Outcome = SurroundOutcome.Failed,
                Message = "Surround would only start if the graphics driver reloaded itself, which would close running games. "
                    + "Switch Surround on once in the NVIDIA control panel, then this profile can do it without a reload.",
            };
        }

        return Set(api, [topo], "switch Surround on", before);
    }

    private SurroundApplyResult Disable(NvApi api, SurroundState before)
    {
        if (!before.IsActive)
        {
            return SurroundApplyResult.Unchanged;
        }

        // Surround goes away by giving every display of every grid its own 1x1 grid again.
        MosaicGridTopoV2[] singles = [.. before.Grids
            .SelectMany(g => g.Displays)
            .Select(d => ToTopo(new SurroundGrid
            {
                Rows = 1,
                Columns = 1,
                Width = 0,
                Height = 0,
                Displays = [d],
            }))];

        return Set(api, singles, "switch Surround off", before);
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
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
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

    /// <summary>Whether a running grid is the one a profile asks for. A refresh rate of 0 means "whatever the driver picked".</summary>
    private static bool Matches(SurroundGrid active, SurroundGrid wanted) =>
        active.Rows == wanted.Rows
        && active.Columns == wanted.Columns
        && active.Width == wanted.Width
        && active.Height == wanted.Height
        && (wanted.RefreshRateHz == 0 || active.RefreshRateHz == wanted.RefreshRateHz)
        && active.Displays.Select(d => d.DisplayId).SequenceEqual(wanted.Displays.Select(d => d.DisplayId));

    private static SurroundGrid ToGrid(in MosaicGridTopoV2 topo)
    {
        List<SurroundDisplay> displays = [];
        for (int cell = 0; cell < topo.DisplayCount && cell < NvApi.MaxMosaicDisplays; cell++)
        {
            displays.Add(new SurroundDisplay { DisplayId = topo.Displays[cell].DisplayId });
        }

        return new SurroundGrid
        {
            Rows = (int)topo.Rows,
            Columns = (int)topo.Columns,
            Width = (int)topo.DisplaySettings.Width,
            Height = (int)topo.DisplaySettings.Height,
            RefreshRateHz = (int)topo.DisplaySettings.Frequency,
            Displays = displays,
        };
    }

    private static MosaicGridTopoV2 ToTopo(SurroundGrid grid)
    {
        MosaicGridTopoV2 topo = default;
        topo.Version = MosaicGridTopoV2.StructVersion;
        topo.Rows = (uint)grid.Rows;
        topo.Columns = (uint)grid.Columns;
        topo.DisplayCount = (uint)grid.Displays.Count;
        topo.Flags = 0;
        for (int cell = 0; cell < grid.Displays.Count && cell < NvApi.MaxMosaicDisplays; cell++)
        {
            topo.Displays[cell].Version = MosaicGridTopoDisplayV2.Version2;
            topo.Displays[cell].DisplayId = grid.Displays[cell].DisplayId;
        }

        topo.DisplaySettings.Version = MosaicDisplaySettingV1.Version1;
        topo.DisplaySettings.Width = (uint)grid.Width;
        topo.DisplaySettings.Height = (uint)grid.Height;
        topo.DisplaySettings.BitsPerPixel = 32;
        topo.DisplaySettings.Frequency = (uint)grid.RefreshRateHz;
        return topo;
    }

    /// <summary>Enough for every display the driver can drive, each as its own grid.</summary>
    private const int MaxGrids = NvApi.MaxDisplays;
}
