using Boatdrill.Core.Settings;

namespace Boatdrill.Core.Scheduling;

/// <summary>When the next scheduled drill is due.</summary>
public static class DrillSchedule
{
    /// <summary>With no drill in the history yet, the first one is due this long after Boatdrill starts.</summary>
    public static readonly TimeSpan FirstDrillDelay = TimeSpan.FromMinutes(10);

    public static TimeSpan? Interval(DrillFrequency frequency) => frequency switch
    {
        DrillFrequency.Daily => TimeSpan.FromDays(1),
        DrillFrequency.Weekly => TimeSpan.FromDays(7),
        DrillFrequency.Monthly => TimeSpan.FromDays(30),
        _ => null,
    };

    /// <summary>
    /// One interval after the last drill that finished, whether it was scheduled or run by hand, so a
    /// drill the user just ran is not repeated. Null when scheduled drills are off. A last drill dated
    /// in the future, after the clock was changed, counts as having run now.
    /// </summary>
    public static DateTimeOffset? NextDue(DrillFrequency frequency, DateTimeOffset? lastDrill, DateTimeOffset now)
    {
        if (Interval(frequency) is not { } interval) return null;
        if (lastDrill is not { } last) return now + FirstDrillDelay;
        return (last > now ? now : last) + interval;
    }
}
