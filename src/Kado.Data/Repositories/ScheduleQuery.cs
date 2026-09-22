using Kado.Core.Recurrence;
using Kado.Data.Models;

namespace Kado.Data.Repositories;

/// <summary>
/// ある日に予定が現れる、その1回ぶん。
/// <para>
/// 繰り返し予定は1件のレコードが何日にも現れ、複数日予定は1件が連続する日にまたがる。
/// カレンダーに並べるには「どの日に、どの予定が」という形に開く必要がある。
/// </para>
/// </summary>
/// <param name="Source">元の予定。編集や削除はこちらを対象にする。</param>
/// <param name="Date">この回が現れる日。</param>
/// <param name="IsContinuation">複数日予定の2日目以降か。先頭の日だけ強調したいときに使う。</param>
/// <param name="IsRecurrence">繰り返しから展開された回か。</param>
public sealed record ScheduledEvent(
    CalendarEvent Source,
    DateOnly Date,
    bool IsContinuation = false,
    bool IsRecurrence = false)
{
    /// <summary>並べ替え用のキー。終日を先に、そのあと開始時刻順。</summary>
    public (int AllDayFirst, TimeOnly Time) SortKey =>
        (Source.IsAllDay ? 0 : 1, Source.StartTime ?? TimeOnly.MinValue);
}

/// <summary>
/// カレンダーに並べるための問い合わせ。
/// <para>
/// リポジトリが返すのは保存されたままの姿（繰り返しは開始日のみ、複数日は開始日と終了日）なので、
/// 日ごとに開く仕事をここで引き受ける。ビュー側でやると月・週・日それぞれに同じ処理が散る。
/// </para>
/// </summary>
public sealed class ScheduleQuery(EventRepository events, TaskRepository tasks)
{
    /// <summary>展開しても壊れないよう、1件の繰り返し予定から取り出す回数に上限を設ける。</summary>
    private const int MaxOccurrencesPerRule = 1000;

    private readonly EventRepository _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly TaskRepository _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));

    /// <summary>
    /// 期間に現れる予定を、日ごとに開いて返す。
    /// </summary>
    public IReadOnlyList<ScheduledEvent> EventsInRange(DateOnly from, DateOnly to)
    {
        if (to < from) (from, to) = (to, from);

        var result = new List<ScheduledEvent>();

        // 単発（複数日を含む）。期間に重なるものを日ごとに開く
        foreach (var source in _events.InRange(from, to))
        {
            var first = source.Date;
            var last = source.LastDate;

            for (var date = Max(first, from); date <= Min(last, to); date = date.AddDays(1))
            {
                result.Add(new ScheduledEvent(source, date, IsContinuation: date != first));
            }
        }

        // 繰り返し。保持しているのは開始日だけなので、規則から日付を起こす
        foreach (var source in _events.AllRecurring())
        {
            if (!RecurrenceRule.TryParse(source.Recurrence, out var rule) || rule is null)
            {
                // 壊れた指定で落とすと、その予定だけでなく月全体が表示できなくなる。
                // 開始日が期間に入っていれば、単発として1回だけ出す
                if (source.Date >= from && source.Date <= to)
                {
                    result.Add(new ScheduledEvent(source, source.Date));
                }
                continue;
            }

            var count = 0;
            foreach (var date in rule.Occurrences(source.Date, from, to))
            {
                if (++count > MaxOccurrencesPerRule) break;
                result.Add(new ScheduledEvent(source, date, IsRecurrence: true));
            }
        }

        return result
            .OrderBy(e => e.Date)
            .ThenBy(e => e.SortKey.AllDayFirst)
            .ThenBy(e => e.SortKey.Time)
            .ThenBy(e => e.Source.Title, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>期間に現れる予定を日付ごとにまとめる。カレンダーのセルに配る用。</summary>
    public IReadOnlyDictionary<DateOnly, IReadOnlyList<ScheduledEvent>> EventsByDate(
        DateOnly from, DateOnly to) =>
        EventsInRange(from, to)
            .GroupBy(e => e.Date)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ScheduledEvent>)g.ToArray());

    /// <summary>期限が期間内にあるタスクを日付ごとにまとめる。</summary>
    public IReadOnlyDictionary<DateOnly, IReadOnlyList<TaskItem>> TasksByDue(DateOnly from, DateOnly to)
    {
        if (to < from) (from, to) = (to, from);

        return _tasks.DueInRange(from, to)
            .GroupBy(t => t.Due!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TaskItem>)g.ToArray());
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
