using System.Globalization;

namespace SlideinaCalendar.Core.Recurrence;

/// <summary>毎日。<c>FREQ=DAILY[;INTERVAL=n]</c></summary>
internal sealed class DailyPattern(int interval) : IRecurrencePattern
{
    public string Frequency => "DAILY";
    public int Interval { get; } = interval;

    public bool Matches(DateOnly date, DateOnly seriesStart) =>
        (date.DayNumber - seriesStart.DayNumber) % Interval == 0;

    public string ToLabel(DateOnly? seriesStart) =>
        Interval == 1 ? "毎日" : $"{Interval}日ごと";

    public string ToSpec() =>
        Interval == 1 ? "FREQ=DAILY" : $"FREQ=DAILY;INTERVAL={Interval}";
}

/// <summary>
/// 毎週（曜日指定）。<c>FREQ=WEEKLY[;INTERVAL=n][;BYDAY=MO,WE]</c>
/// <para>BYDAY を省略した場合は開始日と同じ曜日。週の起点は月曜（RFC 5545 の既定 WKST=MO）。</para>
/// </summary>
internal sealed class WeeklyPattern(int interval, IReadOnlyList<DayOfWeek> byDay) : IRecurrencePattern
{
    public string Frequency => "WEEKLY";
    public int Interval { get; } = interval;
    public IReadOnlyList<DayOfWeek> ByDay { get; } = byDay;

    public bool Matches(DateOnly date, DateOnly seriesStart)
    {
        var weeks = (RecurrenceCodes.WeekStartDayNumber(date)
                     - RecurrenceCodes.WeekStartDayNumber(seriesStart)) / 7;
        if (weeks % Interval != 0) return false;

        return ByDay.Count == 0
            ? date.DayOfWeek == seriesStart.DayOfWeek
            : ByDay.Contains(date.DayOfWeek);
    }

    public string ToLabel(DateOnly? seriesStart)
    {
        var head = Interval == 1 ? "毎週" : $"{Interval}週ごと";

        var days = ByDay.Count > 0
            ? ByDay
            : seriesStart is { } s ? [s.DayOfWeek] : Array.Empty<DayOfWeek>();

        if (days.Count == 0) return head;

        // 日→土の並びに揃えてから連結する（指定順に引きずられないように）
        var names = days.Distinct().OrderBy(d => (int)d).Select(RecurrenceCodes.ToLabel);
        return $"{head} {string.Join("・", names)}";
    }

    public string ToSpec()
    {
        var parts = new List<string> { "FREQ=WEEKLY" };
        if (Interval != 1) parts.Add($"INTERVAL={Interval}");
        if (ByDay.Count > 0)
        {
            parts.Add("BYDAY=" + string.Join(",", ByDay.Distinct().OrderBy(d => (int)d).Select(RecurrenceCodes.ToCode)));
        }
        return string.Join(";", parts);
    }
}

/// <summary>
/// 毎月（日付指定）。<c>FREQ=MONTHLY[;INTERVAL=n][;BYMONTHDAY=15]</c>
/// <para>
/// BYMONTHDAY を省略した場合は開始日と同じ日。負の値は月末からの数えで、<c>-1</c> が月末。
/// 31 日指定の月に 31 日が無い場合、その月はマッチしない（RFC 5545 と同じ挙動）。
/// </para>
/// </summary>
internal sealed class MonthlyPattern(int interval, IReadOnlyList<int> byMonthDay) : IRecurrencePattern
{
    public string Frequency => "MONTHLY";
    public int Interval { get; } = interval;
    public IReadOnlyList<int> ByMonthDay { get; } = byMonthDay;

    public bool Matches(DateOnly date, DateOnly seriesStart)
    {
        var months = (date.Year - seriesStart.Year) * 12 + (date.Month - seriesStart.Month);
        if (months % Interval != 0) return false;

        if (ByMonthDay.Count == 0) return date.Day == seriesStart.Day;

        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        foreach (var spec in ByMonthDay)
        {
            var day = spec >= 0 ? spec : daysInMonth + 1 + spec;   // -1 → 月末
            if (day == date.Day) return true;
        }
        return false;
    }

    public string ToLabel(DateOnly? seriesStart)
    {
        var head = Interval == 1 ? "毎月" : $"{Interval}か月ごと";

        var specs = ByMonthDay.Count > 0
            ? ByMonthDay
            : seriesStart is { } s ? [s.Day] : Array.Empty<int>();

        if (specs.Count == 0) return head;

        var names = specs.Select(v => v == -1 ? "月末" : v < 0 ? $"月末から{-v - 1}日前" : $"{v}日");
        return $"{head} {string.Join("・", names)}";
    }

    public string ToSpec()
    {
        var parts = new List<string> { "FREQ=MONTHLY" };
        if (Interval != 1) parts.Add($"INTERVAL={Interval}");
        if (ByMonthDay.Count > 0) parts.Add("BYMONTHDAY=" + string.Join(",", ByMonthDay));
        return string.Join(";", parts);
    }
}

/// <summary>
/// 毎年。<c>FREQ=YEARLY[;INTERVAL=n][;BYMONTH=9][;BYMONTHDAY=19]</c>
/// <para>省略した部分は開始日の月・日を使う。</para>
/// </summary>
internal sealed class YearlyPattern(int interval, IReadOnlyList<int> byMonth, IReadOnlyList<int> byMonthDay)
    : IRecurrencePattern
{
    public string Frequency => "YEARLY";
    public int Interval { get; } = interval;
    public IReadOnlyList<int> ByMonth { get; } = byMonth;
    public IReadOnlyList<int> ByMonthDay { get; } = byMonthDay;

    public bool Matches(DateOnly date, DateOnly seriesStart)
    {
        if ((date.Year - seriesStart.Year) % Interval != 0) return false;

        var monthOk = ByMonth.Count == 0 ? date.Month == seriesStart.Month : ByMonth.Contains(date.Month);
        if (!monthOk) return false;

        if (ByMonthDay.Count == 0) return date.Day == seriesStart.Day;

        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        foreach (var spec in ByMonthDay)
        {
            var day = spec >= 0 ? spec : daysInMonth + 1 + spec;
            if (day == date.Day) return true;
        }
        return false;
    }

    public string ToLabel(DateOnly? seriesStart)
    {
        var head = Interval == 1 ? "毎年" : $"{Interval}年ごと";

        var months = ByMonth.Count > 0 ? ByMonth : seriesStart is { } s1 ? [s1.Month] : Array.Empty<int>();
        var days = ByMonthDay.Count > 0 ? ByMonthDay : seriesStart is { } s2 ? [s2.Day] : Array.Empty<int>();

        if (months.Count == 0 || days.Count == 0) return head;

        var monthText = string.Join("・", months.Select(m => $"{m}月"));
        var dayText = string.Join("・", days.Select(d => d == -1 ? "月末" : $"{d}日"));
        return $"{head} {monthText}{dayText}";
    }

    public string ToSpec()
    {
        var parts = new List<string> { "FREQ=YEARLY" };
        if (Interval != 1) parts.Add($"INTERVAL={Interval}");
        if (ByMonth.Count > 0) parts.Add("BYMONTH=" + string.Join(",", ByMonth));
        if (ByMonthDay.Count > 0) parts.Add("BYMONTHDAY=" + string.Join(",", ByMonthDay));
        return string.Join(";", parts);
    }
}

/// <summary>組み込みパターンの生成。</summary>
internal static class BuiltInPatterns
{
    public static IRecurrencePattern CreateDaily(RecurrenceParameters p) =>
        new DailyPattern(p.GetPositiveInt("INTERVAL", 1));

    public static IRecurrencePattern CreateWeekly(RecurrenceParameters p) =>
        new WeeklyPattern(p.GetPositiveInt("INTERVAL", 1), p.GetDayList("BYDAY"));

    public static IRecurrencePattern CreateMonthly(RecurrenceParameters p) =>
        new MonthlyPattern(p.GetPositiveInt("INTERVAL", 1), Validate(p.GetIntList("BYMONTHDAY")));

    public static IRecurrencePattern CreateYearly(RecurrenceParameters p) =>
        new YearlyPattern(
            p.GetPositiveInt("INTERVAL", 1),
            ValidateMonths(p.GetIntList("BYMONTH")),
            Validate(p.GetIntList("BYMONTHDAY")));

    private static IReadOnlyList<int> Validate(IReadOnlyList<int> byMonthDay)
    {
        foreach (var v in byMonthDay)
        {
            if (v == 0 || v > 31 || v < -31)
            {
                throw new FormatException(
                    $"BYMONTHDAY は 1〜31 または -1〜-31 で指定してください: {v.ToString(CultureInfo.InvariantCulture)}");
            }
        }
        return byMonthDay;
    }

    private static IReadOnlyList<int> ValidateMonths(IReadOnlyList<int> byMonth)
    {
        foreach (var v in byMonth)
        {
            if (v < 1 || v > 12)
            {
                throw new FormatException(
                    $"BYMONTH は 1〜12 で指定してください: {v.ToString(CultureInfo.InvariantCulture)}");
            }
        }
        return byMonth;
    }
}
