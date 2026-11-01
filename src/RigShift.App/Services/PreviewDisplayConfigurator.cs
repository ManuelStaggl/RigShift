#if DEBUG
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;

namespace RigShift.App.Services;

/// <summary>
/// Debug builds only (<c>RIGSHIFT_PREVIEW_DISPLAYS=&lt;profile name&gt;</c>): reports every display of every profile as
/// attached, with the named profile's displays active in its own arrangement. The machine that takes the screenshots has
/// none of the demo monitors, so every profile would otherwise read "Blocked: … missing" and no page could be shown in a
/// working state. Never touches the real display configuration.
/// </summary>
internal sealed class PreviewDisplayConfigurator(IProfileStore store, string activeProfile) : IDisplayConfigurator
{
    public async Task<DisplaySnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        LoadResult loaded = await store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        Profile? active = loaded.Profiles.FirstOrDefault(p => string.Equals(p.Name, activeProfile, StringComparison.OrdinalIgnoreCase));
        List<AttachedDisplay> displays = [];
        int number = 1;
        foreach (DisplayAssignment assignment in loaded.Profiles.SelectMany(p => p.Displays))
        {
            if (displays.Exists(d => d.Identity == assignment.Identity))
            {
                continue;
            }

            DisplayAssignment? mode = active?.Displays.FirstOrDefault(d => d.Identity == assignment.Identity);
            displays.Add(new AttachedDisplay
            {
                Identity = assignment.Identity,
                IsAvailable = true,
                IsActive = mode is not null,
                ActiveMode = mode,
                WindowsNumber = mode is null ? null : number++,
                NativeHandle = new object(),
            });
        }

        return new DisplaySnapshot { TakenAt = DateTimeOffset.Now, Displays = displays };
    }

    public Task<int> ApplyAsync(TopologyPlan plan, ApplyOptions options, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<int> SetHdrAsync(AttachedDisplay display, bool enabled, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<IReadOnlyList<RefreshRate>> ListRefreshRatesAsync(DisplayIdentity identity, int width, int height, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RefreshRate>>([]);
}
#endif
