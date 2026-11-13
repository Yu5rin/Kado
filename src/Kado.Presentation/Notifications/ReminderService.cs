using System.Globalization;
using System.Text.Json;
using Kado.Core.WorkingDays;
using Kado.Data.Models;
using Kado.Presentation.Settings;

namespace Kado.Presentation.Notifications;

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

    /// <summary>
    /// もう知らせたもの（予定の ID と、その回の日付）。同じ予定を何度も知らせない。
    /// <para>
    /// 片付けは朝のまとめとは切り離し、<b>記録の日付</b>で行う（<see cref="Prune"/>）。
    /// まとめの送信日に結ぶと、日が変わってからまとめの時刻までは毎分消えて、
    /// その間の予定の通知が毎分出た。まとめを切っていると逆に一度も消えず、溜まり続けた。
    /// </para>
    /// </summary>
    private readonly HashSet<(string Id, DateOnly Date, TimeOnly Start)> _notified = [];

    /// <summary>知らせた記録の件数（テスト用）。</summary>
    internal int NotifiedCount => _notified.Count;

    private DateOnly? _summarySentOn;

    /// <summary>
    /// 設定に控えた「もう知らせたもの」を読み込んだか。
    /// <para>
    /// 控えが無いと、アプリを再起動するたびに朝のまとめが出直し、開始前の通知も2回出る。
    /// 初めて見るときに一度だけ読む。
    /// </para>
    /// </summary>
    private bool _restored;

    /// <summary>朝のまとめを出した日を控える設定のキー。</summary>
    internal const string SummarySentOnKey = "reminder.summarySentOn";

    /// <summary>出した開始前の通知（予定の ID と開始日時）を控える設定のキー。当日以降のぶんだけ持つ。</summary>
    internal const string NotifiedKey = "reminder.notified";

    /// <summary>
    /// いま知らせるものがあるか見る。1分ごとに呼ぶ。
    /// </summary>
    /// <param name="now">いまの日時。</param>
    public void Check(DateTime now)
    {
        if (!_notifier.IsSupported) return;

        var today = DateOnly.FromDateTime(now);

        Restore();
        Prune(today);

        if (_settings.SummaryEnabled) CheckSummary(now, today);
        if (_settings.NotifyEnabled) CheckUpcoming(now, today);
    }

    /// <summary>設定に控えた記録を読み込む。読めなければ空のまま（知らせ直すだけで、壊れはしない）。</summary>
    private void Restore()
    {
        if (_restored) return;
        _restored = true;

        try
        {
            if (_workspace.Settings.Get(SummarySentOnKey) is { Length: > 0 } sent &&
                DateOnly.TryParseExact(sent, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var sentOn))
            {
                _summarySentOn = sentOn;
            }

            if (_workspace.Settings.Get(NotifiedKey) is not { Length: > 0 } json) return;

            foreach (var line in JsonSerializer.Deserialize<string[]>(json) ?? [])
            {
                // 日付 \t 開始時刻 \t ID（ID は最後に置き、中に区切りがあっても壊れないようにする）
                var parts = line.Split('\t', 3);

                if (parts.Length == 3 &&
                    DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date) &&
                    TimeOnly.TryParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var start))
                {
                    _notified.Add((parts[2], date, start));
                }
            }
        }
        catch (Exception e) when (e is JsonException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            // 控えが壊れていても、知らせは止めない
        }
    }

    /// <summary>出した開始前の通知を設定に控える。当日以降のぶんだけ（日が変わったら捨てる）。</summary>
    private void SaveNotified(DateOnly today)
    {
        try
        {
            var lines = _notified
                .Where(key => key.Date >= today)
                .OrderBy(key => key.Date).ThenBy(key => key.Start).ThenBy(key => key.Id, StringComparer.Ordinal)
                .Select(key => string.Join(
                    '\t',
                    key.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    key.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
                    key.Id))
                .ToArray();

            _workspace.Settings.Set(NotifiedKey, JsonSerializer.Serialize(lines));
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            // 控えられなくても、知らせは止めない
        }
    }

    /// <summary>
    /// 昨日より前の日付の記録を捨てる。
    /// <para>
    /// 昨日のぶんは残す。23:55 に知らせた翌 0:05 の予定の記録は日付が「明日」なので
    /// 0 時を過ぎても残るが、念のため、日付が変わった直後に見る範囲（今日・明日）の
    /// ひとつ手前まで持つ。
    /// </para>
    /// </summary>
    private void Prune(DateOnly today)
    {
        var oldest = today.AddDays(-1);
        if (_notified.RemoveWhere(key => key.Date < oldest) > 0) SaveNotified(today);
    }

    /// <summary>朝のまとめ。指定した時刻を過ぎていれば、その日は1回だけ出す。</summary>
    private void CheckSummary(DateTime now, DateOnly today)
    {
        if (_summarySentOn == today) return;
        if (TimeOnly.FromDateTime(now) < _settings.SummaryTime) return;

        _summarySentOn = today;

        // 控えておく。再起動のたびに「今日の予定」が出直さないように
        try
        {
            _workspace.Settings.Set(SummarySentOnKey, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            // 控えられなくても、まとめは出す
        }

        // 画面に出していないカレンダーは数に入れない。同じ予定を2つのカレンダーに
        // 持っていると、まとめにも二度出る。
        //
        // <b>「知らせない」設定までは見ない。</b>あれは予定ごとの通知を止める指定で、
        // 朝のまとめは「その日に何があるか」を並べるもの。両方に効かせたら、まとめが
        // 空になった。
        var calendars = _workspace.Sources.Calendars();
        var items = _workspace.Schedule.EventsInRange(today, today)
            .Where(e => !CalendarWorkspace.IsMilestoneMark(e.Source))
            .Where(e => CalendarWorkspace.ShowsEvent(e.Source, calendars))
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

        // カレンダー一覧は、知らせる候補が出てから1回だけ読む（予定ごとに DB を読まない）
        IReadOnlyList<CalendarSource>? calendars = null;

        // 明日の朝いちの予定も拾えるよう、日をまたぐぶんまで見る
        foreach (var scheduled in _workspace.Schedule.EventsInRange(today, today.AddDays(1)))
        {
            var value = scheduled.Source;
            if (value.StartTime is not { } start) continue;
            if (CalendarWorkspace.IsMilestoneMark(value)) continue;

            // 日をまたぐ予定の2日目以降は、始まる予定ではない。開始時刻だけを日付に貼り付けて
            // 数えると、毎日「そろそろ始まる」と知らせてしまう
            if (scheduled.IsContinuation) continue;

            var at = scheduled.Date.ToDateTime(start);
            var when = at - lead;

            // 知らせる時刻を過ぎていて、まだ予定が始まっていないものだけ
            if (when > now || at <= now) continue;

            var key = (value.Id, scheduled.Date, start);
            if (_notified.Contains(key)) continue;

            calendars ??= _workspace.Sources.Calendars();
            if (!CalendarWorkspace.NotifiesFor(value, calendars)) continue;

            _notified.Add(key);
            SaveNotified(today);

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
