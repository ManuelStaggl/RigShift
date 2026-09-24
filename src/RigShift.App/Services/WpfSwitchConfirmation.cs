using System.Windows;
using RigShift.App.Localization;
using RigShift.App.Views;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;

namespace RigShift.App.Services;

/// <summary><see cref="ISwitchConfirmation"/> as a countdown window on the new primary display.</summary>
public sealed class WpfSwitchConfirmation(ProfileCatalog catalog, ActiveProfileMatcher matcher) : ISwitchConfirmation
{
    public Task<ConfirmationResult> ConfirmAsync(Profile profile, DisplaySnapshot before, TimeSpan timeout, CancellationToken cancellationToken) =>
        Application.Current.Dispatcher
            .InvokeAsync(() => ConfirmationWindow.ShowAsync(ViewFor(profile, before), timeout, cancellationToken))
            .Task
            .Unwrap();

    /// <summary>The two pictures and their names; the profile the switch came from is looked up in the old snapshot.</summary>
    private ConfirmationView ViewFor(Profile profile, DisplaySnapshot before)
    {
        ArgumentNullException.ThrowIfNull(profile);
        IReadOnlyList<TopologyDisplay> beforeTopology = before is null
            ? []
            : TopologyDisplays.From(ProfileEditing.CurrentArrangement(before, [], catalog.KnownDisplayNames));
        string beforeName = (before is not null ? matcher.FindActive(catalog.Profiles, before)?.Name : null)
            ?? Loc.Instance["Confirm_Before"];
        return new ConfirmationView(profile.Id, beforeTopology, beforeName, TopologyDisplays.From(profile.Displays), profile.Name);
    }
}
