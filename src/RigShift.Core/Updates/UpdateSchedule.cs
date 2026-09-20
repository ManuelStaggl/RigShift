namespace RigShift.Core.Updates;

/// <summary>When the next update check is due. Pure, so the waiting in the app has nothing left to get wrong.</summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan Regular = TimeSpan.FromHours(24);

    private static readonly TimeSpan[] Retries = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];

    /// <summary>
    /// A failed check is tried again after 1, 5 and 30 minutes – the network is usually just not up yet – and then left
    /// to the regular check, so a machine without internet is not asked forever.
    /// </summary>
    public static TimeSpan NextCheckIn(int failuresInARow) =>
        failuresInARow >= 1 && failuresInARow <= Retries.Length ? Retries[failuresInARow - 1] : Regular;

    /// <summary>
    /// Whether waking from standby is a reason to check: timers do not run while the machine sleeps, so the daily check
    /// of a PC that is only ever suspended would drift or never come.
    /// </summary>
    public static bool IsDueAfterResume(DateTimeOffset? lastSuccess, DateTimeOffset now, int failuresInARow) =>
        failuresInARow > 0 || lastSuccess is not { } last || now - last >= Regular;
}
