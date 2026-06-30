using ExecPlan.Domain.Enums;

namespace ExecPlan.Application.Shifts;

/// <summary>
/// Pure conversion of a UTC instant into the Kuwait shift band (Morning 06-14 / Evening 14-22 /
/// Night 22-06) and the roster date it belongs to. Night shifts that start before midnight and run
/// past it (local hour &lt; 6) still roster against the previous calendar day. No DI dependencies —
/// constructed directly; registration happens when the consuming Application services are wired up.
/// </summary>
public sealed class KuwaitShiftCalculator
{
    private static readonly TimeZoneInfo Tz = ResolveTz();

    private static TimeZoneInfo ResolveTz()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuwait");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Arab Standard Time");
        }
    }

    public ShiftResolution Resolve(DateTime utcNow)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), Tz);
        int h = local.Hour;

        if (h >= 6 && h < 14)
        {
            return new ShiftResolution(ShiftBand.Morning, local.Date);
        }

        if (h >= 14 && h < 22)
        {
            return new ShiftResolution(ShiftBand.Evening, local.Date);
        }

        // Night 22:00-06:00: the after-midnight tail (h < 6) rosters against the previous day.
        var rosterDate = h < 6 ? local.Date.AddDays(-1) : local.Date;
        return new ShiftResolution(ShiftBand.Night, rosterDate);
    }
}
