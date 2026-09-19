using SlideinaCalendar.Data.Repositories;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>
/// 同じ日の予定の並び。
/// <para>
/// 時刻のあるものは<b>時刻の早い順</b>。時刻を持たない予定は時刻で並べられないので、
/// <b>カレンダーの並び順</b>で出す。左パネルで上にあるカレンダーのものが上に来る。
/// 旧 inaCalendar と同じ。
/// </para>
/// <para>
/// 並べ替えは取り出すところ（<c>ScheduleQuery</c>）では決められない。そちらは
/// カレンダーの並び順を知らないため。使う側で最後に並べ直す。
/// </para>
/// </summary>
public static class EventOrder
{
    /// <summary>その日の並びに直す。</summary>
    public static IReadOnlyList<ScheduledEvent> Sort(
        IEnumerable<ScheduledEvent>? events, ICalendarPalette? order)
    {
        if (events is null) return [];

        var by = order ?? DefaultCalendarSources.Instance;

        return events
            // 終日を先に。1日ぶんの枠として先に見えたほうが読みやすい
            .OrderBy(e => e.Source.IsAllDay ? 0 : 1)
            .ThenBy(e => e.Source.IsAllDay ? by.OrderOf(e.Source.CalendarId) : 0)
            .ThenBy(e => e.Source.StartTime ?? TimeOnly.MinValue)
            .ThenBy(e => e.Source.Title, StringComparer.Ordinal)
            .ToArray();
    }
}
