using Kado.Core.WorkingDays;
using Kado.Data.Models;

namespace Kado.Presentation;

/// <summary>
/// 「inaCalendar」に入っている印から稼働日を組み立てる。
/// <para>
/// 旧 inaCalendar は実働日データを Google の inaCalendar に書き出していた。書いているのは
/// <b>例外の日だけ</b>で、平日なのに休む日は「休業日」、土日祝なのに動く日は「特別出勤」。
/// ふつうに稼働する平日には何も書かない。
/// </para>
/// <para>
/// 既定の規則（平日は稼働、土日祝は休み）と突き合わせれば、その月の稼働日がすべて決まる。
/// これを使うと、Excel を持っていない端末でも実働日数が出る。印は同期で渡ってくる。
/// </para>
/// </summary>
public static class WorkingDayMarks
{
    /// <summary>
    /// 印から稼働日を組み立てる。印が1つも無ければ <see cref="WorkingDayCalendar.Empty"/>。
    /// </summary>
    /// <param name="events">「inaCalendar」に入っている予定。</param>
    /// <param name="holidays">祝日。渡さなければ土日だけで判断する。</param>
    public static WorkingDayCalendar Rebuild(
        IEnumerable<CalendarEvent> events, IHolidaySource? holidays = null)
    {
        ArgumentNullException.ThrowIfNull(events);

        var closed = new HashSet<DateOnly>();
        var open = new HashSet<DateOnly>();

        foreach (var value in events)
        {
            if (string.Equals(value.Title, CalendarWorkspace.ClosedDayTitle, StringComparison.Ordinal))
            {
                closed.Add(value.Date);
            }
            else if (string.Equals(value.Title, CalendarWorkspace.OpenDayTitle, StringComparison.Ordinal))
            {
                open.Add(value.Date);
            }
        }

        if (closed.Count == 0 && open.Count == 0) return WorkingDayCalendar.Empty;

        // 登録済みとみなす範囲。印のある最初の月の頭から、最後の月の終わりまで。
        //
        // 印は例外の日にしか付かないので、印の無い月が「ふつうに稼働した月」なのか
        // 「まだ登録していない月」なのかは印だけでは分からない。間にある月は
        // 前者として扱う。月の途中で切ると、その月だけ実働日数が合わなくなる
        var marks = closed.Concat(open).ToArray();
        var from = new DateOnly(marks.Min().Year, marks.Min().Month, 1);
        var last = marks.Max();
        var to = new DateOnly(last.Year, last.Month, DateTime.DaysInMonth(last.Year, last.Month));

        var rest = holidays ?? EmptyHolidaySource.Instance;
        var days = new List<DateOnly>();

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var restDay = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
                          rest.NameOf(date) is { Length: > 0 };

            // 休みのはずの日は「特別出勤」があるときだけ稼働。
            // ふだん働く日は「休業日」があるときだけ休み
            if (restDay ? open.Contains(date) : !closed.Contains(date)) days.Add(date);
        }

        return WorkingDayCalendar.Create(days, from, to, [], null, null);
    }

    /// <summary>
    /// 2つの実働日データを重ねる。
    /// <para>
    /// <paramref name="authoritative"/> を優先する。Excel から読んだものは、印から
    /// 組み立てたものより確かなので、範囲が重なるところでは Excel を採る。
    /// </para>
    /// </summary>
    public static WorkingDayCalendar Overlay(
        WorkingDayCalendar authoritative, WorkingDayCalendar fallback)
    {
        ArgumentNullException.ThrowIfNull(authoritative);
        ArgumentNullException.ThrowIfNull(fallback);

        if (fallback.RangeStart is not { } fs || fallback.RangeEnd is not { } fe) return authoritative;
        if (authoritative.RangeStart is not { } asx || authoritative.RangeEnd is not { } ae) return fallback;

        var days = fallback.Days
            .Where(d => d < asx || d > ae)     // 確かなほうの範囲は触らない
            .Concat(authoritative.Days)
            .Distinct()
            .Order()
            .ToArray();

        return WorkingDayCalendar.Create(
            days,
            asx < fs ? asx : fs,
            ae > fe ? ae : fe,
            authoritative.AllMilestones,
            authoritative.MilestoneRangeStart,
            authoritative.MilestoneRangeEnd);
    }
}
