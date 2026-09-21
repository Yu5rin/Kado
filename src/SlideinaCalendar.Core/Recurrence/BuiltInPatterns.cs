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
/// 毎月（日付指定、または序数つき曜日指定）。
/// <c>FREQ=MONTHLY[;INTERVAL=n][;BYMONTHDAY=15]</c> か
/// <c>FREQ=MONTHLY[;INTERVAL=n];BYDAY=2TU[;BYSETPOS=...]</c>
/// <para>
/// BYMONTHDAY・BYDAY のどちらも省略した場合は開始日と同じ日。負の値は月末からの数えで、
/// <c>-1</c> が月末。31 日指定の月に 31 日が無い場合、その月はマッチしない
/// （RFC 5545 と同じ挙動）。BYMONTHDAY と BYDAY が両方あれば BYMONTHDAY を優先する。
/// </para>
/// <para>
/// BYDAY は「第2火曜」（<c>2TU</c>）のように<b>要素ごとに序数を持てる</b>。第5週が無い月
/// （例: 第5火曜が存在しない月）は、その月だけ該当日が無い扱いにする（例外にしない）。
/// <c>-1</c> は最終、<c>-2</c> は最終の1つ前……という数え方（月末から）。
/// </para>
/// <para>
/// 序数を持たない BYDAY（<c>BYDAY=MO,TU,WE,TH,FR</c> のような曜日の集合）は、
/// <see cref="BySetPos"/>（RFC 5545 の <c>BYSETPOS</c>）と組み合わせて使う。
/// 月内でその曜日集合に該当する日を昇順に並べ、その中の n 番目（負なら末尾から）を選ぶ。
/// 「毎月最終営業日」のような、曜日を問わない指定に使う。
/// </para>
/// </summary>
internal sealed class MonthlyPattern(
    int interval,
    IReadOnlyList<int> byMonthDay,
    IReadOnlyList<(int Ordinal, DayOfWeek Day)> byDay,
    IReadOnlyList<int> bySetPos) : IRecurrencePattern
{
    public string Frequency => "MONTHLY";
    public int Interval { get; } = interval;
    public IReadOnlyList<int> ByMonthDay { get; } = byMonthDay;
    public IReadOnlyList<(int Ordinal, DayOfWeek Day)> ByDay { get; } = byDay;
    public IReadOnlyList<int> BySetPos { get; } = bySetPos;

    public bool Matches(DateOnly date, DateOnly seriesStart)
    {
        var months = (date.Year - seriesStart.Year) * 12 + (date.Month - seriesStart.Month);
        if (months % Interval != 0) return false;

        if (ByMonthDay.Count > 0) return MatchesMonthDay(date);
        if (ByDay.Count > 0) return MatchesByDay(date);

        return date.Day == seriesStart.Day;
    }

    private bool MatchesMonthDay(DateOnly date)
    {
        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        foreach (var spec in ByMonthDay)
        {
            var day = spec >= 0 ? spec : daysInMonth + 1 + spec;   // -1 → 月末
            if (day == date.Day) return true;
        }
        return false;
    }

    private bool MatchesByDay(DateOnly date)
    {
        var ordinalEntries = ByDay.Where(e => e.Ordinal != 0).ToArray();
        if (ordinalEntries.Length > 0)
        {
            foreach (var (ordinal, day) in ordinalEntries)
            {
                if (date.DayOfWeek != day) continue;
                if (OrdinalOf(date, fromStart: ordinal > 0) == Math.Abs(ordinal)) return true;
            }
            // 序数なしの要素が混ざっていれば、そちらは曜日が合えば常に該当する
            return ByDay.Any(e => e.Ordinal == 0 && e.Day == date.DayOfWeek);
        }

        // 序数なし＝曜日の集合。BYSETPOS が無ければ、その曜日はすべて該当する
        var days = ByDay.Select(e => e.Day).ToHashSet();
        if (!days.Contains(date.DayOfWeek)) return false;
        if (BySetPos.Count == 0) return true;

        var candidates = DatesInMonthMatching(date.Year, date.Month, days);
        var index = candidates.IndexOf(date);
        if (index < 0) return false;

        foreach (var pos in BySetPos)
        {
            var resolved = pos > 0 ? pos - 1 : candidates.Count + pos;
            if (resolved == index) return true;
        }
        return false;
    }

    /// <summary>その日が、月内で同じ曜日の何回目か。<paramref name="fromStart"/> なら月初から、でなければ月末から数える。</summary>
    private static int OrdinalOf(DateOnly date, bool fromStart)
    {
        if (fromStart) return (date.Day - 1) / 7 + 1;

        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        return (daysInMonth - date.Day) / 7 + 1;
    }

    /// <summary>その月のうち、指定した曜日集合に当てはまる日を昇順で。</summary>
    private static List<DateOnly> DatesInMonthMatching(int year, int month, IReadOnlySet<DayOfWeek> days)
    {
        var count = DateTime.DaysInMonth(year, month);
        var result = new List<DateOnly>(count);
        for (var d = 1; d <= count; d++)
        {
            var date = new DateOnly(year, month, d);
            if (days.Contains(date.DayOfWeek)) result.Add(date);
        }
        return result;
    }

    public string ToLabel(DateOnly? seriesStart)
    {
        var head = Interval == 1 ? "毎月" : $"{Interval}か月ごと";

        if (ByMonthDay.Count > 0)
        {
            var names = ByMonthDay.Select(v => v == -1 ? "月末" : v < 0 ? $"月末から{-v - 1}日前" : $"{v}日");
            return $"{head} {string.Join("・", names)}";
        }

        if (ByDay.Count > 0)
        {
            var names = ByDay.Select(e => RecurrenceCodes.ToOrdinalLabel(e.Ordinal, e.Day));
            var label = $"{head} {string.Join("・", names)}";
            return BySetPos.Count > 0
                ? $"{label}（{string.Join("・", BySetPos.Select(p => p.ToString(CultureInfo.InvariantCulture)))}番目）"
                : label;
        }

        if (seriesStart is { } s) return $"{head} {s.Day}日";

        return head;
    }

    public string ToSpec()
    {
        var parts = new List<string> { "FREQ=MONTHLY" };
        if (Interval != 1) parts.Add($"INTERVAL={Interval}");
        if (ByMonthDay.Count > 0) parts.Add("BYMONTHDAY=" + string.Join(",", ByMonthDay));
        if (ByDay.Count > 0)
        {
            parts.Add("BYDAY=" + string.Join(",", ByDay.Select(e => RecurrenceCodes.ToOrdinalCode(e.Ordinal, e.Day))));
        }
        if (BySetPos.Count > 0) parts.Add("BYSETPOS=" + string.Join(",", BySetPos));
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
        new MonthlyPattern(
            p.GetPositiveInt("INTERVAL", 1),
            Validate(p.GetIntList("BYMONTHDAY")),
            p.GetOrdinalDayList("BYDAY"),
            p.GetIntList("BYSETPOS"));

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
