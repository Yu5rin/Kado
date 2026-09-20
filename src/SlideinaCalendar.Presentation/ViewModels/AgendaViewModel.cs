using System.Globalization;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Data.Repositories;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>一覧に並ぶ行の種類。</summary>
public enum AgendaRowKind
{
    /// <summary>予定とタスクを持つ日。</summary>
    Day,

    /// <summary>予定の無い日をまとめた行。</summary>
    Gap,
}

/// <summary>
/// 一覧ビューの1行。
/// <para>
/// 予定のある日はその日ぶん、無い日は連続分をまとめて1行にする。
/// </para>
/// </summary>
public sealed class AgendaRowViewModel
{
    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");

    private AgendaRowViewModel(
        AgendaRowKind kind, DateOnly date, DateOnly lastDate,
        IReadOnlyList<DayEventViewModel> events, IReadOnlyList<TaskListItemViewModel> tasks,
        string? holidayName, int? workingDayIndex, bool isToday, int skippedDays)
    {
        Kind = kind;
        Date = date;
        LastDate = lastDate;
        Events = events;
        Tasks = tasks;
        HolidayName = holidayName;
        WorkingDayIndex = workingDayIndex;
        IsToday = isToday;
        SkippedDays = skippedDays;
    }

    public AgendaRowKind Kind { get; }

    /// <summary>この行の先頭の日。</summary>
    public DateOnly Date { get; }

    /// <summary>まとめた行の最後の日。1日ぶんなら <see cref="Date"/> と同じ。</summary>
    public DateOnly LastDate { get; }

    public IReadOnlyList<DayEventViewModel> Events { get; }

    public IReadOnlyList<TaskListItemViewModel> Tasks { get; }

    public string? HolidayName { get; }

    /// <summary>その月の何実働日目か。実働日でなければ null。</summary>
    public int? WorkingDayIndex { get; }

    public bool IsToday { get; }

    /// <summary>まとめた日数。まとめていない行は 0。</summary>
    public int SkippedDays { get; }

    public bool IsGap => Kind == AgendaRowKind.Gap;

    public bool IsSundayLike => Date.DayOfWeek == DayOfWeek.Sunday || HolidayName is not null;

    public bool IsSaturday => Date.DayOfWeek == DayOfWeek.Saturday;

    /// <summary>「9月24日（木）」。まとめた行は「9月25日 〜 9月30日」。</summary>
    public string DateText => IsGap && LastDate > Date
        ? $"{Date.ToString("M月d日", Japanese)} 〜 {LastDate.ToString("M月d日", Japanese)}"
        : Date.ToString("M月d日（ddd）", Japanese);

    /// <summary>「予定なし 6日」。まとめていない行は null。</summary>
    public string? GapText => IsGap
        ? SkippedDays > 1 ? $"予定なし {SkippedDays}日" : "予定なし"
        : null;

    /// <summary>「実働 15日目」。実働日でなければ null。</summary>
    public string? WorkingDayText =>
        WorkingDayIndex is { } index ? $"実働 {index}日目" : null;

    internal static AgendaRowViewModel Day(
        DateOnly date, IReadOnlyList<DayEventViewModel> events, IReadOnlyList<TaskListItemViewModel> tasks,
        string? holidayName, int? workingDayIndex, bool isToday) =>
        new(AgendaRowKind.Day, date, date, events, tasks, holidayName, workingDayIndex, isToday, 0);

    internal static AgendaRowViewModel Gap(DateOnly from, DateOnly to, bool holdsToday) =>
        new(AgendaRowKind.Gap, from, to, [], [], null, null, holdsToday,
            to.DayNumber - from.DayNumber + 1);
}

/// <summary>
/// 一覧（アジェンダ）ビュー（要件書 5.1）。
/// <para>
/// 予定とタスクを時系列で流す。<b>予定のない日は連続分をまとめて1行に畳む。</b>
/// 一般的なカレンダーアプリのように消してしまうと、間がどれだけ空いているのか
/// 分からなくなり、実働日の感覚が飛ぶ。畳んでも日付の連続性は保つ。
/// </para>
/// </summary>
public sealed class AgendaViewModel : ObservableObject
{
    /// <summary>一度に流す日数。ひと月ぶん＋前後の余白。</summary>
    public const int DefaultSpanDays = 60;

    private readonly CalendarWorkspace _workspace;
    private readonly ICalendarSources _sources;
    private readonly DateOnly _today;
    private readonly int _spanDays;

    private DateOnly _from;

    public AgendaViewModel(
        CalendarWorkspace workspace, DateOnly today, ICalendarSources sources,
        int spanDays = DefaultSpanDays)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _today = today;
        _spanDays = Math.Max(1, spanDays);
        _from = today;

        Refresh();
    }

    /// <summary>流し始める日。</summary>
    public DateOnly From
    {
        get => _from;
        set
        {
            if (_from == value) return;

            _from = value;
            Refresh();
            Raise(nameof(From), nameof(HeaderText));
        }
    }

    /// <summary>流し終わる日。</summary>
    public DateOnly To => _from.AddDays(_spanDays - 1);

    /// <summary>「2026年9月20日 〜 11月18日」。</summary>
    public string HeaderText =>
        $"{_from.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("ja-JP"))} 〜 " +
        $"{To.ToString("M月d日", CultureInfo.GetCultureInfo("ja-JP"))}";

    /// <summary>並べる行。</summary>
    public IReadOnlyList<AgendaRowViewModel> Rows { get; private set; } = [];

    /// <summary>1件も無いか。案内を出すのに使う。</summary>
    public bool IsEmpty => Rows.Count == 0 || Rows.All(r => r.IsGap);

    /// <summary>次の期間へ。</summary>
    public void GoToNext() => From = To.AddDays(1);

    /// <summary>前の期間へ。</summary>
    public void GoToPrevious() => From = _from.AddDays(-_spanDays);

    /// <summary>今日から流し直す。</summary>
    public void GoToToday() => From = _today;

    /// <summary>その日から流し直す。</summary>
    public void GoTo(DateOnly date) => From = date;

    /// <summary>データを読み直して並べ直す。</summary>
    public void Refresh()
    {
        var from = _from;
        var to = To;

        var eventsByDate = _workspace.Schedule.EventsByDate(from, to);
        var tasksByDue = _workspace.Schedule.TasksByDue(from, to);

        var rows = new List<AgendaRowViewModel>();

        // 何も無い日が続いたら、まとめて1行にする。ここに控えて、
        // 中身のある日が来たとき（または最後）に吐き出す
        DateOnly? gapFrom = null;
        var gapHoldsToday = false;

        void FlushGap(DateOnly last)
        {
            if (gapFrom is not { } start) return;

            rows.Add(AgendaRowViewModel.Gap(start, last, gapHoldsToday));
            gapFrom = null;
            gapHoldsToday = false;
        }

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            // 左パネルでチェックを外したカレンダーは落とす
            var scheduled = eventsByDate.TryGetValue(date, out var e)
                ? e.Where(x => _sources.IncludesEvent(x.Source) && !CalendarWorkspace.IsMilestoneMark(x.Source))
                    .ToArray()
                : [];

            var due = tasksByDue.TryGetValue(date, out var t)
                ? t.Where(_sources.IncludesTask).ToArray()
                : [];

            if (scheduled.Length == 0 && due.Length == 0)
            {
                gapFrom ??= date;
                gapHoldsToday |= date == _today;
                continue;
            }

            FlushGap(date.AddDays(-1));

            rows.Add(AgendaRowViewModel.Day(
                date,
                scheduled.Select(x => new DayEventViewModel(x, _sources.ColorOf(x.Source.CalendarId))).ToArray(),
                due.Select(Row).ToArray(),
                _workspace.Holidays.NameOf(date),
                _workspace.WorkingDays.IndexInMonth(date),
                date == _today));
        }

        FlushGap(to);

        Rows = rows;
        Raise(nameof(Rows), nameof(IsEmpty));
    }

    private TaskListItemViewModel Row(TaskItem task) =>
        new(task,
            task.Due is { } due ? _workspace.DueFormatter.Format(due, _today) : null,
            // 済んだものには「2実働日 遅れて完了」を添える。右ペインと同じ出し方
            task is { IsDone: true, Due: { } d, CompletedAt: { } at }
                ? _workspace.DueFormatter.FormatDone(d, DateOnly.FromDateTime(at.LocalDateTime))
                : null);
}
