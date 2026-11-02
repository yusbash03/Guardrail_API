namespace Guardrail.Domain.Policy;

/// <summary>Week = ISO week starting Monday 00:00 local; month = calendar month local (SPENDING_POLICY_ENGINE.md §2).</summary>
public static class BudgetPeriods
{
    public static TimeZoneInfo ResolveTimeZone(string timeZoneId) => TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

    public static bool IsValidTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    public static DateTime ToLocal(DateTimeOffset instant, string timeZoneId) =>
        TimeZoneInfo.ConvertTime(instant, ResolveTimeZone(timeZoneId)).DateTime;

    public static DateOnly LocalDate(DateTimeOffset instant, string timeZoneId) => DateOnly.FromDateTime(ToLocal(instant, timeZoneId));

    public static DateOnly WeekStart(DateOnly localDate)
    {
        var offset = ((int)localDate.DayOfWeek + 6) % 7; // Monday = 0
        return localDate.AddDays(-offset);
    }

    public static DateOnly MonthStart(DateOnly localDate) => new(localDate.Year, localDate.Month, 1);

    public static (DateOnly WeekStart, DateOnly MonthStart) PeriodStarts(DateTimeOffset instant, string timeZoneId)
    {
        var local = LocalDate(instant, timeZoneId);
        return (WeekStart(local), MonthStart(local));
    }

    public static DateOnly PeriodEnd(Common.BudgetPeriodType period, DateOnly periodStart) =>
        period == Common.BudgetPeriodType.Weekly ? periodStart.AddDays(6) : periodStart.AddMonths(1).AddDays(-1);
}
