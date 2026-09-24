using System.Windows;
using RigShift.App.Localization;
using RigShift.App.Views;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.Services;

/// <summary><see cref="ISwitchConfirmation"/> as a countdown window on the new primary display.</summary>
public sealed class WpfSwitchConfirmation(ProfileCatalog catalog, ActiveProfileMatcher matcher) : ISwitchConfirmation
{
    /// <summary>
    /// How long past the countdown the switch still waits for the window. Its own timer ends it; this only answers when
    /// that timer never ran – the window did not render after the display change, or the UI thread stands (v4 finding
    /// A-08). Without it the switch waited for good and every later one was refused as "already running".
    /// </summary>
    internal static readonly TimeSpan Slack = TimeSpan.FromSeconds(10);

    public Task<ConfirmationResult> ConfirmAsync(Profile profile, DisplaySnapshot before, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task<ConfirmationResult> answer = Application.Current.Dispatcher
            .InvokeAsync(() => ConfirmationWindow.ShowAsync(ViewFor(profile, before), timeout, cancellationToken))
            .Task
            .Unwrap();
        return WithDeadlineAsync(
            answer, timeout + Slack, TimeProvider.System, () => Application.Current.Dispatcher.InvokeAsync(ConfirmationWindow.CloseOpen), cancellationToken);
    }

    /// <summary>The window's answer, or <see cref="ConfirmationResult.TimedOut"/> – "take it back" – once <paramref name="deadline"/> passed.</summary>
    internal static async Task<ConfirmationResult> WithDeadlineAsync(
        Task<ConfirmationResult> answer, TimeSpan deadline, TimeProvider time, Action expired, CancellationToken cancellationToken)
    {
        try
        {
            return await answer.WaitAsync(deadline, time, cancellationToken);
        }
        catch (TimeoutException)
        {
            Log.Error("The confirmation window did not answer within {Deadline}; the switch is taken back", deadline);
            expired();
            return ConfirmationResult.TimedOut;
        }
    }

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
