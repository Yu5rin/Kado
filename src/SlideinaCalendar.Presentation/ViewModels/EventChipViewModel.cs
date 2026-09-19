using System.Globalization;
using SlideinaCalendar.Data.Repositories;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>
/// 予定の色。カテゴリごとに帯の色を変える。
/// <para>
/// 旧データは色を <c>#rrggbb</c> で持っており、6 種類が使われていた。設定の
/// カテゴリ一覧とも完全には対応していないため、取り込んだ色そのままではなく
/// 系統に寄せて扱う。
/// </para>
/// </summary>
public enum EventAccent
{
    /// <summary>既定。藍。</summary>
    Default,

    /// <summary>緑。</summary>
    Green,

    /// <summary>琥珀。</summary>
    Amber,
}

/// <summary>
/// 月ビューのマスに並べる予定1件。
/// <para>
/// モックでは「09:00 計画レビュー」のように<b>開始時刻を頭に付けて</b>1行で出す。
/// 終日なら時刻を出さない。狭いマスで時刻とタイトルを別々に置くと、どちらも読めなくなる。
/// </para>
/// </summary>
public sealed class EventChipViewModel(ScheduledEvent scheduled)
{
    /// <summary>元の予定。</summary>
    public ScheduledEvent Scheduled { get; } = scheduled;

    public string Id => Scheduled.Source.Id;

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

    /// <summary>帯の色。</summary>
    public EventAccent Accent => ResolveAccent(Scheduled.Source.Color);

    /// <summary>
    /// 色の文字列から系統を決める。
    /// <para>
    /// 旧データで使われていた 6 色を、緑系・琥珀系・それ以外に振り分ける。
    /// 未知の色は既定に倒す。判断できない色を独自に混ぜるより、揃っているほうが読みやすい。
    /// </para>
    /// </summary>
    internal static EventAccent ResolveAccent(string? color) => color?.ToLowerInvariant() switch
    {
        "#d1fae5" or "#cffafe" => EventAccent.Green,
        "#fef3c7" => EventAccent.Amber,
        _ => EventAccent.Default,
    };
}
