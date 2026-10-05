using System.Globalization;
using Kado.Core.WorkingDays;

namespace Kado.Core.Recurrence;

/// <summary>タスクの繰り返しの種類。</summary>
public enum TaskRepeatKind
{
    /// <summary>毎日。</summary>
    Daily,

    /// <summary>毎週（期限日の曜日）。</summary>
    Weekly,

    /// <summary>毎月（期限日の日付。無い月は月末に寄せる）。</summary>
    Monthly,

    /// <summary>毎年（期限日の月日。無い年は月末に寄せる）。</summary>
    Yearly,

    /// <summary>毎週、その週の最初の稼働日。</summary>
    WeekFirstWorkday,

    /// <summary>毎週、その週の最後の稼働日。</summary>
    WeekLastWorkday,

    /// <summary>毎月、その月の最初の稼働日。</summary>
    MonthFirstWorkday,

    /// <summary>毎月、その月の最後の稼働日。</summary>
    MonthLastWorkday,
}

/// <summary>
/// 読めた繰り返しの指定。
/// </summary>
/// <param name="Kind">種類。</param>
/// <param name="Day">毎週の曜日（<see cref="TaskRepeatKind.Weekly"/> のとき）。</param>
/// <param name="Month">毎年の月（<see cref="TaskRepeatKind.Yearly"/> のとき）。</param>
/// <param name="DayOfMonth">毎月・毎年の日（1〜31。<see cref="TaskRepeatKind.Monthly"/>・<see cref="TaskRepeatKind.Yearly"/> のとき）。</param>
public readonly record struct TaskRepeatRule(
    TaskRepeatKind Kind, DayOfWeek Day = DayOfWeek.Sunday, int Month = 0, int DayOfMonth = 0);

/// <summary>
/// タスクの繰り返し（Kado 独自）。指定文字列の読み書きと、次の回の期限の計算。
/// <para>
/// Google Tasks の API には繰り返しの欄が無いので、指定は Kado だけが持ち、Google には送らない。
/// 完了にしたとき、ここで求めた期限で次の回を新しいタスクとして作る。
/// </para>
/// <para>
/// 指定文字列は2系統。暦どおりの4つは RRULE の形（<c>FREQ=WEEKLY;BYDAY=MO</c> など。曜日や日付は
/// <b>期限日から決めて明示して持つ</b>。省略形にすると、遅れて完了したときに系列の起点がずれる）。
/// 稼働日が基準の4つは RRULE では表せないので <c>X-KADO=WEEK-FIRST-WORKDAY</c> のような独自の書き方。
/// 読めない文字列は「繰り返さない」として扱う（<see cref="Parse"/> が null）。呼び出し側は
/// 文字列そのものを消さないこと。
/// </para>
/// </summary>
public static class TaskRepeat
{
    /// <summary>独自の指定のキー。</summary>
    public const string KadoKey = "X-KADO";

    private const string WeekFirst = "WEEK-FIRST-WORKDAY";
    private const string WeekLast = "WEEK-LAST-WORKDAY";
    private const string MonthFirst = "MONTH-FIRST-WORKDAY";
    private const string MonthLast = "MONTH-LAST-WORKDAY";

    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    /// <summary>稼働日基準で、何週・何か月先まで探すか。全部が休みの週・月を飛ばし続けて止まらなくならないための歯止め。</summary>
    private const int MaxPeriods = 120;

    /// <summary>暦どおりの種類か。期限日から曜日・日付を決める。</summary>
    public static bool IsCalendarBased(TaskRepeatKind kind) =>
        kind is TaskRepeatKind.Daily or TaskRepeatKind.Weekly or TaskRepeatKind.Monthly or TaskRepeatKind.Yearly;

    // ----------------------------------------------------------------------
    // 読み書き
    // ----------------------------------------------------------------------

    /// <summary>
    /// 指定文字列を読む。空・読めない・未対応の形（間隔の指定など）は null（繰り返さない）。
    /// <para>並び順と大文字小文字の違いは気にしない。</para>
    /// </summary>
    public static TaskRepeatRule? Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) return null;

            // 同じキーが2回ある指定は、どちらが本物か分からない
            if (!values.TryAdd(part[..eq].Trim().ToUpperInvariant(), part[(eq + 1)..].Trim().ToUpperInvariant()))
            {
                return null;
            }
        }

        if (values.TryGetValue(KadoKey, out var kado))
        {
            if (values.Count != 1) return null;

            return kado switch
            {
                WeekFirst => new TaskRepeatRule(TaskRepeatKind.WeekFirstWorkday),
                WeekLast => new TaskRepeatRule(TaskRepeatKind.WeekLastWorkday),
                MonthFirst => new TaskRepeatRule(TaskRepeatKind.MonthFirstWorkday),
                MonthLast => new TaskRepeatRule(TaskRepeatKind.MonthLastWorkday),
                _ => null,
            };
        }

        if (!values.TryGetValue("FREQ", out var freq)) return null;

        switch (freq)
        {
            case "DAILY" when values.Count == 1:
                return new TaskRepeatRule(TaskRepeatKind.Daily);

            case "WEEKLY" when values.Count == 2 && values.TryGetValue("BYDAY", out var byDay):
                var dayIndex = Array.IndexOf(DayCodes, byDay);
                return dayIndex < 0 ? null : new TaskRepeatRule(TaskRepeatKind.Weekly, (DayOfWeek)dayIndex);

            case "MONTHLY" when values.Count == 2 && values.TryGetValue("BYMONTHDAY", out var monthDay):
                return TryDayOfMonth(monthDay, out var dayOfMonth)
                    ? new TaskRepeatRule(TaskRepeatKind.Monthly, DayOfMonth: dayOfMonth)
                    : null;

            case "YEARLY" when values.Count == 3 &&
                               values.TryGetValue("BYMONTH", out var monthText) &&
                               values.TryGetValue("BYMONTHDAY", out var yearDay):
                if (!int.TryParse(monthText, NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
                    month is < 1 or > 12 ||
                    !TryDayOfMonth(yearDay, out var day))
                {
                    return null;
                }

                // 2月30日のような、どの年にも無い日は読めない指定とする（2月29日は閏年にある）
                return day > DateTime.DaysInMonth(2000, month)
                    ? null
                    : new TaskRepeatRule(TaskRepeatKind.Yearly, Month: month, DayOfMonth: day);

            default:
                return null;
        }
    }

    private static bool TryDayOfMonth(string text, out int day) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out day) && day is >= 1 and <= 31;

    /// <summary>読めた指定を文字列にする。</summary>
    public static string ToSpec(TaskRepeatRule rule) => rule.Kind switch
    {
        TaskRepeatKind.Daily => "FREQ=DAILY",
        TaskRepeatKind.Weekly => $"FREQ=WEEKLY;BYDAY={DayCodes[(int)rule.Day]}",
        TaskRepeatKind.Monthly => $"FREQ=MONTHLY;BYMONTHDAY={rule.DayOfMonth.ToString(CultureInfo.InvariantCulture)}",
        TaskRepeatKind.Yearly =>
            $"FREQ=YEARLY;BYMONTH={rule.Month.ToString(CultureInfo.InvariantCulture)};" +
            $"BYMONTHDAY={rule.DayOfMonth.ToString(CultureInfo.InvariantCulture)}",
        TaskRepeatKind.WeekFirstWorkday => $"{KadoKey}={WeekFirst}",
        TaskRepeatKind.WeekLastWorkday => $"{KadoKey}={WeekLast}",
        TaskRepeatKind.MonthFirstWorkday => $"{KadoKey}={MonthFirst}",
        TaskRepeatKind.MonthLastWorkday => $"{KadoKey}={MonthLast}",
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    /// <summary>
    /// 種類から指定文字列を作る。暦どおりの4つは <paramref name="due"/> の曜日・日付・月日で明示する。
    /// </summary>
    public static string ToSpec(TaskRepeatKind kind, DateOnly due) => ToSpec(kind switch
    {
        TaskRepeatKind.Weekly => new TaskRepeatRule(kind, Day: due.DayOfWeek),
        TaskRepeatKind.Monthly => new TaskRepeatRule(kind, DayOfMonth: due.Day),
        TaskRepeatKind.Yearly => new TaskRepeatRule(kind, Month: due.Month, DayOfMonth: due.Day),
        _ => new TaskRepeatRule(kind),
    });

    /// <summary>
    /// 期限日が変わったあとの指定。暦どおりの4つは新しい期限日の曜日・日付で指定し直す。
    /// 稼働日基準のものと、読めない指定はそのまま返す（読めない指定は消さない）。
    /// </summary>
    public static string? Respec(string? spec, DateOnly due) =>
        Parse(spec) is { } rule && IsCalendarBased(rule.Kind) ? ToSpec(rule.Kind, due) : spec;

    // ----------------------------------------------------------------------
    // 次の回
    // ----------------------------------------------------------------------

    /// <summary>
    /// 次の回の期限。<b>今の期限より後</b>で、かつ<b>今日以降</b>の、いちばん早い該当日。
    /// <para>
    /// 溜めてから完了しても、過去の日付の回は作らない（今日以降の最初の該当日に飛ぶ）。
    /// 早く完了したときは、今の期限より後の最初の回になる（前倒しで完了しても、今の期限と同じ週は返さない）。
    /// </para>
    /// <para>
    /// 毎月・毎年は、その月・年に指定の日が無ければ月末に寄せる。寄せても次の月・年は指定の日に戻る
    /// （指定を持ったまま計算するので、31日が28日・30日にずれていかない）。RFC 5545 の BYMONTHDAY は
    /// 日の無い月を飛ばすので、この2つは <see cref="RecurrenceRule"/> を使わず自前で数える。
    /// </para>
    /// <para>
    /// 稼働日基準は、該当する週・月を順に見て、条件を満たす最初の日を返す。稼働日が1日も無い週・月は飛ばす。
    /// </para>
    /// </summary>
    /// <param name="spec">指定文字列。読めなければ null を返す。</param>
    /// <param name="due">今の期限。</param>
    /// <param name="today">今日。</param>
    /// <param name="isWorkday">稼働日か。渡さなければ土日祝を除いた日（<see cref="WorkdayRule.Default"/>）。</param>
    /// <param name="weekStart">週の始まりの曜日（設定）。週の区切りに使う。</param>
    /// <returns>次の期限。繰り返さない指定・読めない指定・見つからないときは null。</returns>
    public static DateOnly? NextDue(
        string? spec, DateOnly due, DateOnly today,
        Func<DateOnly, bool>? isWorkday = null, DayOfWeek weekStart = DayOfWeek.Sunday)
    {
        if (Parse(spec) is not { } rule) return null;

        // ここより前の日は返さない。今の期限の次の日と今日の遅いほう
        var tomorrowOfDue = due.AddDays(1);
        var lower = tomorrowOfDue > today ? tomorrowOfDue : today;

        // 日付の上限に近いところでは数えない（DateOnly があふれて例外になる）
        if (lower.Year > 9990) return null;

        var workday = isWorkday ?? WorkdayRule.Default.IsWorkday;

        return rule.Kind switch
        {
            TaskRepeatKind.Daily or TaskRepeatKind.Weekly => FirstOccurrence(rule, due, lower),
            TaskRepeatKind.Monthly => NextMonthly(rule.DayOfMonth, lower),
            TaskRepeatKind.Yearly => NextYearly(rule.Month, rule.DayOfMonth, lower),
            TaskRepeatKind.WeekFirstWorkday => NextWeekly(lower, weekStart, workday, first: true),
            TaskRepeatKind.WeekLastWorkday => NextWeekly(lower, weekStart, workday, first: false),
            TaskRepeatKind.MonthFirstWorkday => NextMonthlyWorkday(lower, workday, first: true),
            TaskRepeatKind.MonthLastWorkday => NextMonthlyWorkday(lower, workday, first: false),
            _ => null,
        };
    }

    /// <summary>毎日・毎週は <see cref="RecurrenceRule"/> に数えさせる。</summary>
    private static DateOnly? FirstOccurrence(TaskRepeatRule rule, DateOnly due, DateOnly lower)
    {
        var recurrence = RecurrenceRule.Parse(ToSpec(rule));

        // 毎週は7日以内に必ず当たる
        foreach (var date in recurrence.Occurrences(due, lower, lower.AddDays(7))) return date;

        return null;
    }

    private static DateOnly NextMonthly(int dayOfMonth, DateOnly lower)
    {
        var month = new DateOnly(lower.Year, lower.Month, 1);

        while (true)
        {
            // 無い日（31日、2月29日など）は、その月の末日に寄せる
            var day = Math.Min(dayOfMonth, DateTime.DaysInMonth(month.Year, month.Month));
            var candidate = new DateOnly(month.Year, month.Month, day);
            if (candidate >= lower) return candidate;

            month = month.AddMonths(1);
        }
    }

    private static DateOnly NextYearly(int month, int dayOfMonth, DateOnly lower)
    {
        for (var year = lower.Year; ; year++)
        {
            var day = Math.Min(dayOfMonth, DateTime.DaysInMonth(year, month));
            var candidate = new DateOnly(year, month, day);
            if (candidate >= lower) return candidate;
        }
    }

    private static DateOnly? NextWeekly(DateOnly lower, DayOfWeek weekStart, Func<DateOnly, bool> isWorkday, bool first)
    {
        var start = lower.AddDays(-(((int)lower.DayOfWeek - (int)weekStart + 7) % 7));

        for (var i = 0; i < MaxPeriods; i++, start = start.AddDays(7))
        {
            var found = first ? FirstWorkday(start, 7, isWorkday) : LastWorkday(start, 7, isWorkday);

            // 全部が休みの週は飛ばす。今の期限・今日より前の日も飛ばす
            if (found is { } date && date >= lower) return date;
        }

        return null;
    }

    private static DateOnly? NextMonthlyWorkday(DateOnly lower, Func<DateOnly, bool> isWorkday, bool first)
    {
        var start = new DateOnly(lower.Year, lower.Month, 1);

        for (var i = 0; i < MaxPeriods; i++, start = start.AddMonths(1))
        {
            var length = DateTime.DaysInMonth(start.Year, start.Month);
            var found = first ? FirstWorkday(start, length, isWorkday) : LastWorkday(start, length, isWorkday);

            if (found is { } date && date >= lower) return date;
        }

        return null;
    }

    private static DateOnly? FirstWorkday(DateOnly start, int length, Func<DateOnly, bool> isWorkday)
    {
        for (var i = 0; i < length; i++)
        {
            var date = start.AddDays(i);
            if (isWorkday(date)) return date;
        }

        return null;
    }

    private static DateOnly? LastWorkday(DateOnly start, int length, Func<DateOnly, bool> isWorkday)
    {
        for (var i = length - 1; i >= 0; i--)
        {
            var date = start.AddDays(i);
            if (isWorkday(date)) return date;
        }

        return null;
    }
}
