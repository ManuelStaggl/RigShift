using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Keeps the active profile's desktop icon positions current: moving icons is enough, nobody has to remember "Save
/// positions". Windows reshuffles the icons itself whenever the displays change, so nothing is saved while a switch
/// runs, for <see cref="SettleTime"/> after a switch or a display change, or while no profile matches the screens.
/// </summary>
public sealed class DesktopIconWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Longer than a switch's tidy-up, which puts the profile's icons back over several seconds.</summary>
    internal static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(20);

    private readonly ProfileCatalog _catalog;
    private readonly SwitchCoordinator _coordinator;
    private readonly IDesktopIcons _icons;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };
    private DateTimeOffset _quietUntil;
    private DesktopIconLayout? _previous;
    private bool _checking;

    public DesktopIconWatcher(ProfileCatalog catalog, SwitchCoordinator coordinator, IDesktopIcons icons, TimeProvider time, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _catalog = catalog;
        _coordinator = coordinator;
        _icons = icons;
        _time = time;
        _log = log.ForContext<DesktopIconWatcher>();
        _coordinator.PropertyChanged += OnCoordinatorChanged;
        _coordinator.FollowUpCompleted += OnFollowUpCompleted;
        _timer.Tick += OnTick;

        // Right after sign-in Explorer may still be laying the desktop out.
        Quiet();
    }

    /// <summary>Raised on the UI thread after a moved layout was saved, with the time of the save.</summary>
    public event EventHandler<DateTimeOffset>? Saved;

    public void Start() => _timer.Start();

    /// <summary>The displays changed: Explorer moves the icons now, which is not the user's arrangement.</summary>
    public void Quiet()
    {
        _quietUntil = _time.GetUtcNow() + SettleTime;
        _previous = null;
    }

    /// <summary>One look at the desktop; saves the layout to the active profile once it changed and held still.</summary>
    public async Task CheckAsync()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        try
        {
            if (!MaySave() || _catalog.ActiveProfile is not { RememberDesktopIcons: true } active)
            {
                _previous = null;
                return;
            }

            // The shell call waits up to ten seconds for a hanging Explorer; never on the UI thread.
            DesktopIconLayout? now = await Task.Run(_icons.Capture);
            if (now is not { IsEmpty: false })
            {
                _previous = null;
                return;
            }

            // Only a layout that held for one interval: the user may still be dragging, Explorer still arranging.
            bool held = now.SamePositions(_previous);
            _previous = now;
            if (!held || !MaySave() || _catalog.ActiveProfile?.Id != active.Id || _catalog.Find(active.Id) is not { RememberDesktopIcons: true } stored
                || now.SamePositions(stored.DesktopIcons))
            {
                return;
            }

            await _catalog.SaveAsync(stored with { DesktopIcons = now }, CancellationToken.None);
            _log.Information("Desktop icons of {Profile} saved after they were moved: {Count}", stored.Name, now.Icons.Count);
            Saved?.Invoke(this, _time.GetLocalNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Moved desktop icons could not be saved to the active profile");
        }
        finally
        {
            _checking = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _coordinator.PropertyChanged -= OnCoordinatorChanged;
        _coordinator.FollowUpCompleted -= OnFollowUpCompleted;
    }

    private bool MaySave() => !_coordinator.IsSwitching && _time.GetUtcNow() >= _quietUntil;

    private async void OnTick(object? sender, EventArgs e) => await CheckAsync();

    private void OnCoordinatorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SwitchCoordinator.IsSwitching))
        {
            Quiet();
        }
    }

    private void OnFollowUpCompleted(object? sender, SwitchRecord e) => Quiet();
}
