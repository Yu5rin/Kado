using System.Globalization;
using Kado.Data.Repositories;

namespace Kado.Presentation.ViewModels;

/// <summary>
/// 複数日にまたがる予定の、この日の位置。
/// <para>
/// 角を丸めるのは本当の端だけにする。1日ずつ丸めると、隣り合ったマスに別々の予定が
/// 並んでいるように見える。
/// </para>
/// </summary>
public enum ChipSpan
{
    /// <summary>1日で終わる予定。</summary>
    Single,

    /// <summary>またがる予定の初日。</summary>
    Start,

    /// <summary>またがる予定の途中の日。</summary>
    Middle,

    /// <summary>またがる予定の最終日。</summary>
    End,
}

/// <summary>
/// 月ビューのマスに並べる予定1件。
/// <para>
/// モックでは「09:00 計画レビュー」のように<b>開始時刻を頭に付けて</b>1行で出す。
/// 終日なら時刻を出さない。狭いマスで時刻とタイトルを別々に置くと、どちらも読めなくなる。
/// </para>
/// </summary>
/// <param name="scheduled">この日に現れる1回ぶん。</param>
/// <param name="color">所属カレンダーの色（<c>#rrggbb</c>）。</param>
/// <param name="showsTitle">
/// この日にタイトルを出すか。
/// <para>
/// またがる予定の途中の日には出さない。毎日タイトルを繰り返すと、同じ予定が日ごとに
/// 別々に入っているように見える。週をまたいで続くときは、週の頭でもう一度出す。
/// </para>
/// </param>
public sealed class EventChipViewModel(
    ScheduledEvent scheduled, string? color = null, bool showsTitle = true)
{
    /// <summary>元の予定。</summary>
    public ScheduledEvent Scheduled { get; } = scheduled;

    public string Id => Scheduled.Source.Id;

    /// <summary>
    /// 帯の色（<c>#rrggbb</c>）。所属カレンダーで決まる。
    /// <para>null なら表示側が既定のアクセント色を使う。</para>
    /// </summary>
    public string? Color { get; } = color;

    /// <summary>この日がまたがりのどこにあたるか。</summary>
    public ChipSpan Span { get; } = SpanOf(scheduled);

    /// <summary>この日にタイトルを出すか。端の日は頼まれなくても出す。</summary>
    public bool ShowsTitle { get; } =
        showsTitle || SpanOf(scheduled) is ChipSpan.Single or ChipSpan.Start;

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

    /// <summary>マスに出す文字。またがりの途中では空にして、帯を切らさない。</summary>
    public string Text => ShowsTitle ? Label : string.Empty;

    /// <summary>ツールチップ。マスでは省略されるので全文を出す。</summary>
    public string Tooltip => Scheduled.Source.Location is { Length: > 0 } location
        ? $"{Label}（{location}）"
        : Label;

    private static ChipSpan SpanOf(ScheduledEvent scheduled)
    {
        // 繰り返しから起こした回は、元の終了日を見ても意味がない。
        // 規則が持っているのは1回ぶんの長さではなく、最初の回の終わり
        if (scheduled.IsRecurrence) return ChipSpan.Single;

        var source = scheduled.Source;
        if (source.LastDate <= source.Date) return ChipSpan.Single;

        if (!scheduled.IsContinuation) return ChipSpan.Start;

        return scheduled.Date >= source.LastDate ? ChipSpan.End : ChipSpan.Middle;
    }
}
