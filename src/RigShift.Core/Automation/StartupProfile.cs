using RigShift.Core.Profiles;
using RigShift.Core.Settings;

namespace RigShift.Core.Automation;

/// <summary>Why <see cref="StartupProfile.Choose"/> picked no profile, or <see cref="Apply"/>.</summary>
public enum StartupProfileDecision
{
    Apply,
    TurnedOff,
    NoDefaultProfile,
    AlreadyActive,

    /// <summary>A USB rule's devices are all connected: the rig is in use, the default profile would undo it.</summary>
    RuleDevicesConnected,
}

/// <summary>
/// The default profile when RigShift starts with Windows (issue #13). Windows restores the layout the PC was shut down
/// in; with the setting on, the PC starts in the default profile instead.
/// </summary>
public static class StartupProfile
{
    /// <param name="present">Connected USB devices as <c>VID_xxxx&amp;PID_xxxx</c>.</param>
    public static (StartupProfileDecision Decision, Profile? Profile) Choose(
        AppSettings settings, IEnumerable<Profile> profiles, Guid? activeProfileId, IReadOnlySet<string> present)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(present);

        if (!settings.ApplyDefaultProfileWithWindows)
        {
            return (StartupProfileDecision.TurnedOff, null);
        }

        if (settings.DefaultProfileId is not { } id || profiles.FirstOrDefault(p => p.Id == id) is not { } profile)
        {
            return (StartupProfileDecision.NoDefaultProfile, null);
        }

        if (activeProfileId == id)
        {
            return (StartupProfileDecision.AlreadyActive, profile);
        }

        bool ruleRunning = !settings.AutomationPaused && (settings.AutomationRules ?? [])
            .Where(r => !AutomationTrigger.IsIgnored(r))
            .Select(AutomationTrigger.WatchedDevicesOf)
            .Any(devices => devices.Count > 0 && devices.All(present.Contains));
        return ruleRunning ? (StartupProfileDecision.RuleDevicesConnected, profile) : (StartupProfileDecision.Apply, profile);
    }
}
