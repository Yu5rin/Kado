using Kado.Data.Repositories;

namespace Kado.Presentation.ViewModels;

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
    private readonly ICalendarSources _sources;

    public TimelineBuilder(CalendarWorkspace workspace, ICalendarSources? sources = null,
        TimeOnly? dayStart = null, TimeOnly? dayEnd = null, double hourHeight = WeekHourHeight)
    {
        HourHeight = hourHeight;
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _sources = sources ?? DefaultCalendarSources.Instance;

        // 表示時間帯は設定で変えられる（要件書 5.5）。既定はモックと同じ 8〜20 時
        DayStart = dayStart ?? new TimeOnly(8, 0);
        DayEnd = dayEnd ?? new TimeOnly(20, 0);
    }

    /// <summary>
    /// 1時間分の高さの下限。
    /// <para>
    /// 時間帯は画面の高さに割り付けるが、24時間ぶんとなると1時間が潰れる。ここまで
    /// 縮めても収まらないぶんはスクロールに回す。予定の題が1行読める高さが目安。
    /// </para>
    /// </summary>
    public const double MinHourHeight = 26;

    /// <summary>1時間分の高さ。</summary>
    public double HourHeight { get; }

    /// <summary>時間軸の上端の時刻。</summary>
    public TimeOnly DayStart { get; }

    /// <summary>時間軸の下端の時刻。</summary>
    public TimeOnly DayEnd { get; }

    /// <summary>
    /// 時間軸に並ぶ時間の数。
    /// <para>
    /// 端数は切り上げる。真夜中までを出すときの下端は 23:59 で持つので（<c>TimeOnly</c> は
    /// 24:00 を表せない）、切り捨てると 23 時台がまるごと落ちる。
    /// </para>
    /// </summary>
    public int HourCount => Math.Max((int)Math.Ceiling((DayEnd - DayStart).TotalHours), 1);

    /// <summary>時間軸全体の高さ。</summary>
    public double TimelineHeight => HourCount * HourHeight;

    /// <summary>左に出す「8:00」などの見出し。</summary>
    public IReadOnlyList<string> HourLabels =>
        Enumerable.Range(DayStart.Hour, HourCount).Select(h => $"{h}:00").ToArray();

    /// <summary>
    /// 1時間の高さだけを変えた複製。
    /// <para>使える高さは表示側にしか分からないので、測ってから作り直す。</para>
    /// </summary>
    public TimelineBuilder WithHourHeight(double hourHeight) =>
        new(_workspace, _sources, DayStart, DayEnd, hourHeight);

    /// <summary>
    /// 使える高さに時間帯を割り付けたときの、1時間の高さ。
    /// <para><see cref="MinHourHeight"/> より縮めない。残りはスクロールで見る。</para>
    /// </summary>
    public double HourHeightFor(double available) =>
        Math.Max(available / HourCount, MinHourHeight);

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

        var columns = new List<WeekDayColumnViewModel>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var all = eventsByDate.TryGetValue(date, out var e) ? e : null;
            var events = EventOrder.Sort(all?.Where(x => _sources.IncludesEvent(x.Source)), _sources);
            var tasks = tasksByDue.TryGetValue(date, out var t)
                ? t.Where(_sources.IncludesTask).ToArray() : [];

            columns.Add(new WeekDayColumnViewModel(
                date, today, _workspace.WorkingDays, _workspace.Holidays.NameOf(date),
                // 終日はレーン、時刻付きは時間軸と、置き場所が違う
                events.Where(x => x.Source.IsAllDay).ToArray(),
                tasks,
                Layout(events),
                _sources,
                MilestoneRow.For(date, all, _sources)));
        }

        return columns;
    }

    /// <summary>時刻付きの予定を、時間軸の座標に置き換える。</summary>
    private IReadOnlyList<TimeBlockViewModel> Layout(IReadOnlyList<ScheduledEvent> events)
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
                _sources.ColorOf(scheduled.Source.CalendarId),
                scheduled.Source.Location));
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
