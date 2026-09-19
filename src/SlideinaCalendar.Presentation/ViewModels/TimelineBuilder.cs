using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Data.Repositories;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>
/// 時間軸に並べる列を組み立てる。
/// <para>
/// 週ビューと日ビューは、列の本数以外は同じものを出す（要件書 5.1・5.4）。
/// 置き場所の計算を両方に持たせると、片方だけ直して食い違う。
/// </para>
/// </summary>
public sealed class TimelineBuilder
{
    /// <summary>週ビューの1時間分の高さ。モックの <c>.hcell</c> と同じ。</summary>
    public const double WeekHourHeight = 44;

    /// <summary>
    /// 日ビューの1時間分の高さ。モックの <c>.dayline .hcell</c> と同じ。
    /// <para>列が1本しかないぶん、縦に余裕を取れる。</para>
    /// </summary>
    public const double DayHourHeight = 56;

    /// <summary>ブロックの下を詰める量。詰めないと下の罫線に乗って読みにくい。</summary>
    private const double BlockGap = 3;

    private readonly CalendarWorkspace _workspace;
    private readonly ISourceFilter _filter;

    public TimelineBuilder(CalendarWorkspace workspace, ISourceFilter? filter = null,
        TimeOnly? dayStart = null, TimeOnly? dayEnd = null, double hourHeight = WeekHourHeight)
    {
        HourHeight = hourHeight;
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _filter = filter ?? ShowAllFilter.Instance;

        // 表示時間帯は設定で変えられる（要件書 5.5）。既定はモックと同じ 8〜20 時
        DayStart = dayStart ?? new TimeOnly(8, 0);
        DayEnd = dayEnd ?? new TimeOnly(20, 0);
    }

    /// <summary>1時間分の高さ。</summary>
    public double HourHeight { get; }

    /// <summary>時間軸の上端の時刻。</summary>
    public TimeOnly DayStart { get; }

    /// <summary>時間軸の下端の時刻。</summary>
    public TimeOnly DayEnd { get; }

    /// <summary>時間軸全体の高さ。</summary>
    public double TimelineHeight => (DayEnd.Hour - DayStart.Hour) * HourHeight;

    /// <summary>左に出す「8:00」などの見出し。</summary>
    public IReadOnlyList<string> HourLabels =>
        Enumerable.Range(DayStart.Hour, DayEnd.Hour - DayStart.Hour).Select(h => $"{h}:00").ToArray();

    /// <summary>表示時間帯に入っている時刻か。現在時刻の線を出すかどうかの判断に使う。</summary>
    public bool Covers(TimeOnly time) => time >= DayStart && time < DayEnd;

    /// <summary>時間軸の上端からの位置。</summary>
    public double OffsetOf(TimeOnly time) =>
        (time.ToTimeSpan() - DayStart.ToTimeSpan()).TotalHours * HourHeight;

    /// <summary>期間ぶんの列を組み立てる。</summary>
    public IReadOnlyList<WeekDayColumnViewModel> Build(DateOnly from, DateOnly to, DateOnly today)
    {
        var eventsByDate = _workspace.Schedule.EventsByDate(from, to);
        var tasksByDue = _workspace.Schedule.TasksByDue(from, to);
        var blocksByDate = _workspace.Tasks.BlocksInRange(from, to)
            .GroupBy(b => b.Date)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<WorkBlock>)g.ToArray());

        // 作業時間ブロックはタスクの題を持たない。引くために一度だけ読む
        var titles = _workspace.Tasks.All().ToDictionary(t => t.Id, t => t.Title, StringComparer.Ordinal);

        var columns = new List<WeekDayColumnViewModel>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var events = eventsByDate.TryGetValue(date, out var e)
                ? e.Where(x => _filter.IncludesEvent(x.Source)).ToArray() : [];
            var tasks = tasksByDue.TryGetValue(date, out var t)
                ? t.Where(_filter.IncludesTask).ToArray() : [];
            var blocks = blocksByDate.TryGetValue(date, out var b) ? b : [];

            columns.Add(new WeekDayColumnViewModel(
                date, today, _workspace.WorkingDays, _workspace.Holidays.NameOf(date),
                // 終日はレーン、時刻付きは時間軸と、置き場所が違う
                events.Where(x => x.Source.IsAllDay).ToArray(),
                tasks,
                Layout(events, blocks, titles)));
        }

        return columns;
    }

    /// <summary>時刻付きの予定と作業時間ブロックを、時間軸の座標に置き換える。</summary>
    private IReadOnlyList<TimeBlockViewModel> Layout(
        IReadOnlyList<ScheduledEvent> events, IReadOnlyList<WorkBlock> blocks,
        IReadOnlyDictionary<string, string> taskTitles)
    {
        var result = new List<TimeBlockViewModel>();

        foreach (var scheduled in events)
        {
            if (scheduled.Source.StartTime is not { } start) continue;

            // 終了時刻が無い予定は 30 分の枠で置く。潰れて読めないのを避ける
            var end = scheduled.Source.EndTime ?? start.AddMinutes(30);
            if (Place(start, end) is not { } placed) continue;

            result.Add(new TimeBlockViewModel(
                scheduled.Source.Id, scheduled.Source.Title, start, end,
                placed.Top, placed.Height,
                EventChipViewModel.ResolveAccent(scheduled.Source.Color),
                isWorkBlock: false, scheduled.Source.Location));
        }

        foreach (var block in blocks)
        {
            if (Place(block.StartTime, block.EndTime) is not { } placed) continue;

            result.Add(new TimeBlockViewModel(
                block.Id, taskTitles.GetValueOrDefault(block.TaskId, "（削除されたタスク）"),
                block.StartTime, block.EndTime, placed.Top, placed.Height,
                EventAccent.Default, isWorkBlock: true, location: null));
        }

        return result.OrderBy(b => b.Start).ToArray();
    }

    /// <summary>
    /// 置き場所を決める。表示時間帯からはみ出す分は端で切る。
    /// <para>まるごと外れているものは置かない。</para>
    /// </summary>
    private (double Top, double Height)? Place(TimeOnly start, TimeOnly end)
    {
        if (end <= DayStart || start >= DayEnd) return null;

        var top = OffsetOf(start < DayStart ? DayStart : start);
        var bottom = OffsetOf(end > DayEnd ? DayEnd : end);

        return (top, Math.Max(bottom - top - BlockGap, HourHeight / 4));
    }
}
