using Avalonia.Threading;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Avalonia.Views;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.Avalonia.Services;

/// <summary><see cref="ISwitchConfirmation"/> as a countdown window on the new primary display.</summary>
internal sealed class AvaloniaSwitchConfirmation(ProfileCatalog catalog, ActiveProfileMatcher matcher) : ISwitchConfirmation, IOpenConfirmation
{
    // Fallback for when the window's own timer never ran; without it a lost window blocked every later switch.
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(10);

    public async Task<ConfirmationResult> ConfirmAsync(Profile profile, DisplaySnapshot before, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task<ConfirmationResult> answer = Dispatcher.UIThread.InvokeAsync(() => ConfirmationWindow.ShowAsync(ViewFor(profile, before), timeout, cancellationToken));
        try
        {
            return await answer.WaitAsync(timeout + Slack, cancellationToken);
        }
        catch (TimeoutException)
        {
            Log.Error("The confirmation window did not answer within {Deadline}; the switch is taken back", timeout + Slack);
            Dispatcher.UIThread.Post(ConfirmationWindow.CloseOpen);
            return ConfirmationResult.TimedOut;
        }
    }

    public bool TryConfirm(Guid profileId) => ConfirmationWindow.TryConfirm(profileId);

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
