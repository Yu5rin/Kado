using System.Globalization;
using Kado.Core.Recurrence;
using Kado.Core.WorkingDays;
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

/// <summary>予定表に出すタスクの見た目。</summary>
public enum ScheduledTaskLook
{
    /// <summary>ふつうの見た目。未完了は通常、完了は取り消し線（今までの完了の見た目）。</summary>
    Normal,

    /// <summary>
    /// 薄い見た目。遅れて完了したタスクが、期限日のマスに残す跡。
    /// 「このマスが期限だった」ことは見えるが、実際に片付けたのは別の日（完了日）。
    /// </summary>
    Faint,
}

/// <summary>
/// ある日に、タスクがどんな見た目・添え書きで現れるか。<see cref="ScheduledEvent"/> のタスク版。
/// <para>
/// タスクは期限日にだけ出るわけではない。完了したタスクは完了した日に出て、遅れて完了したものは
/// 期限日にも薄く跡を残す。どの日に・どの見た目で・どんな添え書きで出すかを、この型に1か所で
/// 持たせ、月・週・日・一覧・右ペインはこれを描くだけにする（決めるのは
/// <see cref="ScheduleQuery.PlaceTasks"/>）。
/// </para>
/// </summary>
/// <param name="Source">元のタスク。編集・完了の切り替え・削除はこちらを対象にする。</param>
/// <param name="Date">出す日。</param>
/// <param name="Look">見た目（通常／薄い）。</param>
/// <param name="Note">
/// 添え書き。無ければ null。遅れて完了した日には「期限 9/7・4実働日遅れ」、
/// 期限日に残る跡には「9/11 完了」。
/// </param>
public sealed record ScheduledTask(
    TaskItem Source,
    DateOnly Date,
    ScheduledTaskLook Look = ScheduledTaskLook.Normal,
    string? Note = null)
{
    public string Id => Source.Id;

    public string Title => Source.Title;

    public bool IsDone => Source.IsDone;

    /// <summary>薄い見た目か。</summary>
    public bool IsFaint => Look == ScheduledTaskLook.Faint;

    /// <summary>薄い跡に添える短い添え書き（「9/11 完了」）。通常の見た目では null。</summary>
    public string? FaintNote => IsFaint ? Note : null;

    /// <summary>チップのツールチップ。題名に添え書きがあれば続ける。</summary>
    public string Tooltip => Note is { Length: > 0 } ? $"{Title}\n{Note}" : Title;
}

/// <summary>
/// カレンダーに並べるための問い合わせ。
/// <para>
/// リポジトリが返すのは保存されたままの姿（繰り返しは開始日のみ、複数日は開始日と終了日）なので、
/// 日ごとに開く仕事をここで引き受ける。ビュー側でやると月・週・日それぞれに同じ処理が散る。
/// </para>
/// </summary>
public sealed class ScheduleQuery(EventRepository events, TaskRepository tasks, TimeZoneInfo? timeZone = null)
{
    /// <summary>展開しても壊れないよう、1件の繰り返し予定から取り出す回数に上限を設ける。</summary>
    private const int MaxOccurrencesPerRule = 1000;

    private readonly EventRepository _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly TaskRepository _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));

    /// <summary>
    /// 完了日時を「日付」にするときのタイムゾーン。端末のローカル。
    /// <para>
    /// 作った時点の <see cref="TimeZoneInfo.Local"/> を握らない。何日も動き続けるあいだに端末の
    /// タイムゾーンが変わる（出張・夏時間・設定の変更）と、握ったままでは古いゾーンで日付を出し続ける。
    /// 毎回 <see cref="TimeZoneInfo.Local"/> を見る（変更の通知を受けたアプリが
    /// <see cref="TimeZoneInfo.ClearCachedData"/> を呼べば、次から新しいゾーンになる）。
    /// 引数で渡されたときだけ、そのゾーンに固定する（テスト用）。
    /// </para>
    /// </summary>
    private TimeZoneInfo TimeZone => timeZone ?? TimeZoneInfo.Local;

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

    /// <summary>
    /// 期間に現れるタスクを、日付ごとにまとめる。カレンダーのセルに配る用。
    /// <para>どの日にどう出すかの決まりは <see cref="PlaceTasks"/> にある。</para>
    /// </summary>
    /// <param name="from">期間の初日。</param>
    /// <param name="to">期間の末日。</param>
    /// <param name="formatter">遅れの数え方（実働日か暦日か）を持つ。添え書きの日数はこれで数える。</param>
    public IReadOnlyDictionary<DateOnly, IReadOnlyList<ScheduledTask>> TasksByDate(
        DateOnly from, DateOnly to, DueDateFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (to < from) (from, to) = (to, from);

        return PlaceTasks(_tasks.InRange(from, to), from, to, formatter)
            .GroupBy(p => p.Date)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ScheduledTask>)g.ToArray());
    }

    /// <summary>
    /// タスクを、期間のどの日に・どの見た目・どの添え書きで出すかに開く。
    /// <para>
    /// <b>未完了</b>は期限日に、ふつうの見た目で出す。<b>完了したタスク</b>は
    /// <b>完了した日</b>（<see cref="TaskItem.CompletedAt"/> を端末のタイムゾーンに直した日付）に出す。
    /// 遅れて完了したものには「期限 9/7・4実働日遅れ」と添え、期限日にも<b>薄く</b>
    /// 「9/11 完了」と跡を残す。期限どおり・前倒しで完了したものは、期限日には出さない。
    /// </para>
    /// <para>
    /// 完了日が分からない完了タスク（<c>CompletedAt</c> の無い古いデータ）は、今までどおり
    /// 期限日に出す。期限の無い完了タスクは完了日に出す（完了日も無ければ出ない）。
    /// </para>
    /// <para>
    /// 遅れの数え方は右ペインの「遅れて完了」と同じ <see cref="DueDateFormatter.FormatDone"/>。
    /// 実働日で数えるか暦日で数えるかの設定に従う。
    /// </para>
    /// <para>この関数は保存内容を変えない。表示のための組み替えだけをする。</para>
    /// </summary>
    /// <param name="tasks">候補。期間に無いものが混じっていてもよい（ここで落とす）。</param>
    /// <param name="from">期間の初日。</param>
    /// <param name="to">期間の末日。</param>
    /// <param name="formatter">遅れの数え方（実働日か暦日か）を持つ。添え書きの日数はこれで数える。</param>
    public IReadOnlyList<ScheduledTask> PlaceTasks(
        IEnumerable<TaskItem> tasks, DateOnly from, DateOnly to, DueDateFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(formatter);
        if (to < from) (from, to) = (to, from);

        var result = new List<ScheduledTask>();

        foreach (var task in tasks)
        {
            var completed = CompletedDate(task);

            // 未完了、または完了した日が分からない完了タスクは、期限日に出す
            if (!task.IsDone || completed is null)
            {
                if (task.Due is { } due && InRange(due, from, to))
                {
                    result.Add(new ScheduledTask(task, due));
                }
                continue;
            }

            var done = task.Due is { } d ? formatter.FormatDone(d, completed.Value) : null;
            var isLate = done is { Kind: DoneKind.Late };

            // 完了した日。遅れたときだけ期限と遅れを添える。
            // 期限どおり・前倒しは、右ペインと同じ「期限どおり完了」「3実働日 早く完了」を添える
            if (InRange(completed.Value, from, to))
            {
                var note = isLate ? LateNote(task.Due!.Value, completed.Value, done!) : done?.Text;
                result.Add(new ScheduledTask(task, completed.Value, Note: note));
            }

            // 遅れて完了したぶんは、期限日にも薄く残す。完了日と同じ日なら重ねて出さない
            if (isLate && task.Due is { } late && late != completed.Value && InRange(late, from, to))
            {
                result.Add(new ScheduledTask(
                    task, late, ScheduledTaskLook.Faint,
                    $"{Short(completed.Value, late)} 完了"));
            }
        }

        return result
            .OrderBy(p => p.Date)
            // 薄い跡はその日の末尾。今日やることの邪魔をしない
            .ThenBy(p => p.IsFaint)
            .ThenBy(p => p.Source.SortOrder)
            .ThenBy(p => p.Source.CreatedAt)
            .ThenBy(p => p.Source.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 完了した日。<see cref="TaskItem.CompletedAt"/> を端末のタイムゾーンに直した日付。
    /// 未完了、または完了日時が無ければ null。
    /// </summary>
    public DateOnly? CompletedDate(TaskItem task) =>
        task is { IsDone: true, CompletedAt: { } at }
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, TimeZone).DateTime)
            : null;

    /// <summary>「期限 9/7・4実働日遅れ」。数え方は <see cref="DoneText"/> が持っている。</summary>
    private static string LateNote(DateOnly due, DateOnly completed, DoneText done) =>
        $"期限 {Short(due, completed)}・{done.Days}{(done.IsCalendarUnit ? "日" : "実働日")}遅れ";

    /// <summary>「9/7」。<paramref name="reference"/> と年が違うときだけ「2025/9/7」。</summary>
    private static string Short(DateOnly date, DateOnly reference) =>
        date.ToString(date.Year == reference.Year ? "M/d" : "yyyy/M/d", CultureInfo.InvariantCulture);

    private static bool InRange(DateOnly date, DateOnly from, DateOnly to) => date >= from && date <= to;

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
