using System.Globalization;
using SlideinaCalendar.Data.Repositories;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>
/// 月ビューのマスに並べる予定1件。
/// <para>
/// モックでは「09:00 計画レビュー」のように<b>開始時刻を頭に付けて</b>1行で出す。
/// 終日なら時刻を出さない。狭いマスで時刻とタイトルを別々に置くと、どちらも読めなくなる。
/// </para>
/// </summary>
public sealed class EventChipViewModel(ScheduledEvent scheduled, string? color = null)
{
    /// <summary>元の予定。</summary>
    public ScheduledEvent Scheduled { get; } = scheduled;

    public string Id => Scheduled.Source.Id;

    /// <summary>
    /// 帯の色（<c>#rrggbb</c>）。所属カレンダーで決まる。
    /// <para>null なら表示側が既定のアクセント色を使う。</para>
    /// </summary>
    public string? Color { get; } = color;

    /// <summary>「09:00 計画レビュー」。終日と、複数日の2日目以降は時刻を出さない。</summary>
    public string Label
    {
        get
        {
            var title = Scheduled.Source.Title;

            // 継続中の日に開始時刻を出すと、その日に始まるように見える
            if (Scheduled.IsContinuation || Scheduled.Source.StartTime is not { } start) return title;

            return $"{start.ToString("HH:mm", CultureInfo.InvariantCulture)} {title}";
        }
    }

    /// <summary>ツールチップ。マスでは省略されるので全文を出す。</summary>
    public string Tooltip => Scheduled.Source.Location is { Length: > 0 } location
        ? $"{Label}（{location}）"
        : Label;
}
