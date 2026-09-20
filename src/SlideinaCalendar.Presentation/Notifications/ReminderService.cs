using System.Globalization;
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

        var items = _workspace.Schedule.EventsInRange(today, today)
            .Where(e => !CalendarWorkspace.IsMilestoneMark(e.Source))
            .OrderBy(e => e.Source.StartTime ?? TimeOnly.MinValue)
            .ThenBy(e => e.Source.Title, StringComparer.Ordinal)
            .ToArray();

        var heading = $"今日の予定（{today.ToString("M/d", CultureInfo.InvariantCulture)}）";

        if (items.Length == 0)
        {
            _notifier.Notify(heading, "予定はありません");
            return;
        }

        var lines = items
            .Take(SummaryLimit)
            .Select(e => e.Source.StartTime is { } at
                ? $"{at.ToString("HH:mm", CultureInfo.InvariantCulture)} {e.Source.Title}"
                : e.Source.Title);

        var more = items.Length > SummaryLimit ? $"\n…ほか {items.Length - SummaryLimit} 件" : string.Empty;

        _notifier.Notify($"{heading} {items.Length}件", string.Join("\n", lines) + more);
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

            var key = $"{value.Id}|{scheduled.Date:yyyy-MM-dd}";
            if (!_notified.Add(key)) continue;

            _notifier.Notify(value.Title, Detail(value, start));
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
