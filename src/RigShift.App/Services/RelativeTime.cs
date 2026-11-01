using System.Globalization;
using RigShift.App.Localization;

namespace RigShift.App.Services;

/// <summary>"just now", "12 min ago", "today 21:40", "yesterday 19:12", then date and time – for the switch history.</summary>
public static class RelativeTime
{
    public static string Format(DateTimeOffset at, DateTimeOffset now, CultureInfo culture)
    {
        TimeSpan age = now - at;
        if (age < TimeSpan.FromMinutes(1))
        {
            return Loc.Instance["Time_JustNow"];
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Loc.Format("Time_MinutesAgo", (int)age.TotalMinutes);
        }

        DateTime local = at.ToLocalTime().DateTime;
        DateTime today = now.ToLocalTime().Date;
        string time = local.ToString("t", culture);
        if (local.Date == today)
        {
            return Loc.Format("Time_Today", time);
        }

        if (local.Date == today.AddDays(-1))
        {
            return Loc.Format("Time_Yesterday", time);
        }

        return local.ToString("d", culture) + " " + time;
    }
}
