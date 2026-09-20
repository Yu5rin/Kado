using SlideinaCalendar.Data.Repositories;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>
/// 月ビューの帯の、1マスぶんの1段。
/// <para>
/// またがる予定は、週のあいだ<b>同じ段</b>に居続けないと繋がって見えない。日によって
/// 上下すると、1日ずつ切れているように見える。段を確保するため、その日に何も無くても
/// 空の枠を置く。
/// </para>
/// </summary>
public sealed class EventBandViewModel
{
    private EventBandViewModel(EventChipViewModel? chip) => Chip = chip;

    /// <summary>この段に出す予定。空の枠なら null。</summary>
    public EventChipViewModel? Chip { get; }

    /// <summary>場所を空けているだけか。</summary>
    public bool IsEmpty => Chip is null;

    /// <summary>予定を置いた段。</summary>
    public static EventBandViewModel For(EventChipViewModel chip) => new(chip);

    /// <summary>場所を空けるだけの段。</summary>
    public static EventBandViewModel Empty { get; } = new(null);
}

/// <summary>
/// またがる予定を、週の行ごとに段へ割り付ける。
/// <para>
/// 重なる予定が同じ段に来ないよう、上から順に空いている段へ入れる。始まりが早いものを
/// 上にするので、長い予定が上、あとから始まる短い予定が下に並ぶ。
/// </para>
/// </summary>
public static class EventBands
{
    /// <summary>
    /// 日ごとの段を組み立てる。
    /// </summary>
    /// <param name="from">並べ始める日。<b>週の頭でなければならない。</b>7日ずつ区切る。</param>
    /// <param name="to">並べ終わる日。</param>
    /// <param name="eventsByDate">日ごとの予定。左パネルのチェックを通したあとのもの。</param>
    /// <param name="palette">カレンダーの色引き。</param>
    public static IReadOnlyDictionary<DateOnly, IReadOnlyList<EventBandViewModel>> Build(
        DateOnly from, DateOnly to,
        IReadOnlyDictionary<DateOnly, IReadOnlyList<ScheduledEvent>> eventsByDate,
        ICalendarPalette? palette = null)
    {
        var result = new Dictionary<DateOnly, IReadOnlyList<EventBandViewModel>>();

        for (var rowStart = from; rowStart <= to; rowStart = rowStart.AddDays(7))
        {
            var rowEnd = Min(rowStart.AddDays(6), to);

            foreach (var (date, bands) in BuildRow(rowStart, rowEnd, eventsByDate, palette))
            {
                result[date] = bands;
            }
        }

        return result;
    }

    /// <summary>週1行ぶん。</summary>
    private static Dictionary<DateOnly, IReadOnlyList<EventBandViewModel>> BuildRow(
        DateOnly rowStart, DateOnly rowEnd,
        IReadOnlyDictionary<DateOnly, IReadOnlyList<ScheduledEvent>> eventsByDate,
        ICalendarPalette? palette)
    {
        // この行に現れるまたがり予定を、予定ごとにまとめる。
        // 同じ予定が日ごとに別の ScheduledEvent として来るので、1本にたたむ
        var spans = new Dictionary<string, List<ScheduledEvent>>(StringComparer.Ordinal);

        for (var date = rowStart; date <= rowEnd; date = date.AddDays(1))
        {
            if (!eventsByDate.TryGetValue(date, out var events)) continue;

            foreach (var scheduled in events.Where(IsMultiDay))
            {
                if (!spans.TryGetValue(scheduled.Source.Id, out var days))
                {
                    spans[scheduled.Source.Id] = days = [];
                }

                days.Add(scheduled);
            }
        }

        var result = new Dictionary<DateOnly, IReadOnlyList<EventBandViewModel>>();
        if (spans.Count == 0) return result;

        // 早く始まるものを上に。同じ日に始まるなら長いほうを上にすると、
        // 短い予定が長い予定を上下に分断しない
        var ordered = spans.Values
            .Select(days => days.OrderBy(d => d.Date).ToArray())
            .OrderBy(days => days[0].Date)
            .ThenByDescending(days => days.Length)
            .ThenBy(days => days[0].Source.Title, StringComparer.Ordinal)
            .ThenBy(days => days[0].Source.Id, StringComparer.Ordinal)
            .ToArray();

        // 段ごとに「そこまで埋まっている日」を持つ。空いている最初の段へ入れる
        var lanes = new List<DateOnly>();
        var placed = new List<(int Lane, ScheduledEvent[] Days)>();

        foreach (var days in ordered)
        {
            var start = days[0].Date;

            var lane = lanes.FindIndex(filledTo => filledTo < start);
            if (lane < 0)
            {
                lane = lanes.Count;
                lanes.Add(default);
            }

            lanes[lane] = days[^1].Date;
            placed.Add((lane, days));
        }

        for (var date = rowStart; date <= rowEnd; date = date.AddDays(1))
        {
            var bands = new EventBandViewModel[lanes.Count];
            Array.Fill(bands, EventBandViewModel.Empty);

            foreach (var (lane, days) in placed)
            {
                if (days.FirstOrDefault(d => d.Date == date) is not { } scheduled) continue;

                // 週をまたいで続くときは、行の頭でもう一度タイトルを出す。
                // 出さないと、前の週を見ない限り何の予定か分からない
                bands[lane] = EventBandViewModel.For(new EventChipViewModel(
                    scheduled, palette?.ColorOf(scheduled.Source.CalendarId),
                    showsTitle: date == rowStart));
            }

            result[date] = bands;
        }

        return result;
    }

    /// <summary>
    /// 帯にするか。
    /// <para>繰り返しから起こした回は、元の終了日を見ても1回ぶんの長さにならないので含めない。</para>
    /// </summary>
    private static bool IsMultiDay(ScheduledEvent scheduled) =>
        !scheduled.IsRecurrence && scheduled.Source.LastDate > scheduled.Source.Date;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
