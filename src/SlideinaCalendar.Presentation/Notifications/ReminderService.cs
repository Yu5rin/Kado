using System.Globalization;
using SlideinaCalendar.Core.WorkingDays;
using SlideinaCalendar.Presentation.Settings;

namespace SlideinaCalendar.Presentation.Notifications;

/// <summary>
/// 予定の前と、朝のまとめを知らせる。
/// <para>
/// <b>先の時刻に仕掛けるのではなく、1分ごとに「いま知らせるものがあるか」を見る。</b>
/// 仕掛ける方式は、予定が増減したり時刻が変わったりするたびに全部やり直す必要があり、
/// スリープから戻ったときの取りこぼしも起きる。
/// </para>
/// </summary>
public sealed class ReminderService(CalendarWorkspace workspace, AppSettings settings, INotifier notifier)
{
    private readonly CalendarWorkspace _workspace =
        workspace ?? throw new ArgumentNullException(nameof(workspace));

    private readonly AppSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly INotifier _notifier = notifier ?? NullNotifier.Instance;

    /// <summary>もう知らせたもの。同じ予定を何度も知らせない。</summary>
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);

    private DateOnly? _summarySentOn;

    /// <summary>
    /// いま知らせるものがあるか見る。1分ごとに呼ぶ。
    /// </summary>
    /// <param name="now">いまの日時。</param>
    public void Check(DateTime now)
    {
        if (!_notifier.IsSupported) return;

        var today = DateOnly.FromDateTime(now);

        // 日をまたいだら、知らせた記録を捨てる。溜め続ける意味がない
        if (_summarySentOn is { } sent && sent != today) _notified.Clear();

        if (_settings.SummaryEnabled) CheckSummary(now, today);
        if (_settings.NotifyEnabled) CheckUpcoming(now, today);
    }

    /// <summary>朝のまとめ。指定した時刻を過ぎていれば、その日は1回だけ出す。</summary>
    private void CheckSummary(DateTime now, DateOnly today)
    {
        if (_summarySentOn == today) return;
        if (TimeOnly.FromDateTime(now) < _settings.SummaryTime) return;

        _summarySentOn = today;

        // 画面に出していないカレンダーは数に入れない。同じ予定を2つのカレンダーに
        // 持っていると、まとめにも二度出る。
        //
        // <b>「知らせない」設定までは見ない。</b>あれは予定ごとの通知を止める指定で、
        // 朝のまとめは「その日に何があるか」を並べるもの。両方に効かせたら、まとめが
        // 空になった。
        var items = _workspace.Schedule.EventsInRange(today, today)
            .Where(e => !CalendarWorkspace.IsMilestoneMark(e.Source))
            .Where(e => _workspace.ShowsEvent(e.Source))
            .DistinctBy(e => (e.Source.StartTime, e.Source.Title))
            .OrderBy(e => e.Source.StartTime ?? TimeOnly.MinValue)
            .ThenBy(e => e.Source.Title, StringComparer.Ordinal)
            .ToArray();

        var heading = $"今日の予定（{today.ToString("M/d", CultureInfo.InvariantCulture)}）";

        // タスクの期限通知（要件書 7.5）はここで、朝のまとめに「今日まで／遅れ」の数行を
        // 足す形で出す。絞り込みは右ペイン（SelectedDayViewModel.Tasks）と同じ、
        // 未完了・期限あり・期限順。以前この右ペインの絞り込みを取り違えて
        // 「本日の予定はありません」になった不具合があるので、ここでも同じ轍を踏まない
        // よう、予定とタスクを別々に数える（両方0件でも「予定はありません」だけを言う）
        var dueLines = DueTaskLines(today);

        if (items.Length == 0 && dueLines.Count == 0)
        {
            _notifier.Notify(heading, "予定はありません", _settings.NotifySound);
            return;
        }

        var lines = items
            .Take(SummaryLimit)
            .Select(e => e.Source.StartTime is { } at
                ? $"{at.ToString("HH:mm", CultureInfo.InvariantCulture)} {e.Source.Title}"
                : e.Source.Title)
            .ToList();

        if (items.Length == 0) lines.Add("予定はありません");

        var more = items.Length > SummaryLimit ? $"\n…ほか {items.Length - SummaryLimit} 件" : string.Empty;

        if (dueLines.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("タスク");
            lines.AddRange(dueLines.Take(SummaryLimit));

            if (dueLines.Count > SummaryLimit) lines.Add($"…ほか {dueLines.Count - SummaryLimit} 件");
        }

        _notifier.Notify(
            $"{heading} {items.Length}件", string.Join("\n", lines) + more, _settings.NotifySound);
    }

    /// <summary>
    /// 「今日まで」「遅れ」のタスクを期限の近い順に並べる。
    /// <para>
    /// 絞り込みは右ペイン（<c>SelectedDayViewModel.Tasks</c>）と同じ、未完了・期限あり・
    /// 期限順。表示を切ったタスクリストのものは数えない。
    /// </para>
    /// </summary>
    private IReadOnlyList<string> DueTaskLines(DateOnly today)
    {
        var hiddenTaskLists = _workspace.Sources.TaskLists()
            .Where(list => !list.IsVisible)
            .Select(list => list.Id)
            .ToHashSet(StringComparer.Ordinal);

        return _workspace.Tasks.All()
            .Where(t => t.TaskListId is not { Length: > 0 } id || !hiddenTaskLists.Contains(id))
            .Where(t => !t.IsDone && t.HasDue)
            .OrderBy(t => t.Due)
            .ThenBy(t => t.Title, StringComparer.Ordinal)
            .Select(t => (Task: t, Due: _workspace.DueFormatter.Format(t.Due!.Value, today)))
            // 「残り 3実働日」のような先の予告までは出さない。まとめに載せるのは
            // 今日と遅れ（超過）だけ
            .Where(x => x.Due.Kind is DueKind.Today or DueKind.Overdue)
            .Select(x => $"{x.Due.Text} {x.Task.Title}")
            .ToArray();
    }

    /// <summary>そろそろ始まる予定。設定した分だけ前に知らせる。</summary>
    private void CheckUpcoming(DateTime now, DateOnly today)
    {
        var lead = TimeSpan.FromMinutes(_settings.NotifyLeadMinutes);

        // 明日の朝いちの予定も拾えるよう、日をまたぐぶんまで見る
        foreach (var scheduled in _workspace.Schedule.EventsInRange(today, today.AddDays(1)))
        {
            var value = scheduled.Source;
            if (value.StartTime is not { } start) continue;
            if (CalendarWorkspace.IsMilestoneMark(value)) continue;

            var at = scheduled.Date.ToDateTime(start);
            var when = at - lead;

            // 知らせる時刻を過ぎていて、まだ予定が始まっていないものだけ
            if (when > now || at <= now) continue;

            if (!_workspace.NotifiesFor(value)) continue;

            var key = $"{value.Id}|{scheduled.Date:yyyy-MM-dd}";
            if (!_notified.Add(key)) continue;

            _notifier.Notify(value.Title, Detail(value, start), _settings.NotifySound);
        }
    }

    private static string Detail(Data.Models.CalendarEvent value, TimeOnly start)
    {
        var span = value.EndTime is { } end
            ? $"{start.ToString("HH:mm", CultureInfo.InvariantCulture)}–{end.ToString("HH:mm", CultureInfo.InvariantCulture)}"
            : start.ToString("HH:mm", CultureInfo.InvariantCulture);

        return value.Location is { Length: > 0 } place ? $"{span} @{place}" : span;
    }

    /// <summary>まとめに並べる上限。これ以上は通知に収まらない。</summary>
    private const int SummaryLimit = 8;
}
