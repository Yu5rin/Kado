using System.Globalization;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Presentation.Infrastructure;

namespace Kado.Presentation.ViewModels;

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
public sealed class AgendaRowViewModel : ObservableObject
{
    private bool _isSelected;

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

    /// <summary>選んでいる行か。押したときに面で示す。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

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
/// <para>
/// <b>持っているぶんを全部出す。</b>はじめは60日ずつ送る作りにしていたが、一覧は
/// 「端から端まで眺める」ための面なので、区切ると探しているものが隣の期間にある
/// ことになって使いにくい。畳みが効くので、何年ぶんあっても行数はそれほど増えない。
/// </para>
/// </summary>
public sealed class AgendaViewModel : ObservableObject
{
    /// <summary>
    /// 何も無いときに出す前後の日数。
    /// <para>データが1件も無くても、今日のまわりが空であることは見せる。</para>
    /// </summary>
    public const int EmptySpanDays = 30;

    /// <summary>
    /// 今日から前後に出す年数の上限。
    /// <para>
    /// 持っているぶんを全部出すといっても、限度は要る。日をなぞる処理が日数ぶん
    /// 回るうえ、<b>繰り返しの予定はその期間ぶんすべて展開される</b>。10年ぶんに
    /// していたら目に見えて重くなった。前後2年あれば、実用では「全部」に足りる。
    /// </para>
    /// </summary>
    private const int MaxYears = 2;

    private readonly CalendarWorkspace _workspace;
    private readonly ICalendarSources _sources;
    private readonly DateOnly _today;

    private DateOnly _from;
    private DateOnly _to;
    private DateOnly _selectedDate;

    public AgendaViewModel(CalendarWorkspace workspace, DateOnly today, ICalendarSources sources)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _today = today;
        _selectedDate = today;

        Refresh();
    }

    /// <summary>流し始める日。持っているデータの先頭。</summary>
    public DateOnly From => _from;

    /// <summary>流し終わる日。持っているデータの末尾。</summary>
    public DateOnly To => _to;

    /// <summary>「2023年1月5日 〜 2027年3月31日」。</summary>
    public string HeaderText =>
        $"{_from.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("ja-JP"))} 〜 " +
        $"{_to.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("ja-JP"))}";

    /// <summary>並べる行。</summary>
    public IReadOnlyList<AgendaRowViewModel> Rows { get; private set; } = [];

    /// <summary>1件も無いか。案内を出すのに使う。</summary>
    public bool IsEmpty => Rows.Count == 0 || Rows.All(r => r.IsGap);

    /// <summary>
    /// その日を含む行。
    /// <para>開いたときに今日のあたりへ送るために使う。</para>
    /// </summary>
    public AgendaRowViewModel? RowOn(DateOnly date) =>
        Rows.FirstOrDefault(r => r.Date <= date && date <= r.LastDate);

    /// <summary>今日を含む行。</summary>
    public AgendaRowViewModel? TodayRow => RowOn(_today);

    /// <summary>
    /// 選んでいる日。
    /// <para>その日を含む行に印を付ける。畳んだ行の中の日を選んでも、その行が光る。</para>
    /// </summary>
    public DateOnly SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (_selectedDate == value) return;

            _selectedDate = value;
            MarkSelected();
            Raise(nameof(SelectedDate));
        }
    }

    /// <summary>どの行が選ばれているかを反映する。</summary>
    private void MarkSelected()
    {
        foreach (var row in Rows)
        {
            row.IsSelected = row.Date <= _selectedDate && _selectedDate <= row.LastDate;
        }
    }

    /// <summary>
    /// その日のあたりまで送ってほしい。
    /// <para>
    /// 全部出しているので並べ直しは要らない。動かすのは画面のスクロール位置だけ
    /// なので、表示側に頼む。
    /// </para>
    /// </summary>
    public event EventHandler<DateOnly>? ScrollRequested;

    /// <summary>その日のあたりへ送る。</summary>
    public void GoTo(DateOnly date) => ScrollRequested?.Invoke(this, date);

    /// <summary>今日のあたりへ送る。</summary>
    public void GoToToday() => GoTo(_today);

    /// <summary>データを読み直して並べ直す。</summary>
    public void Refresh()
    {
        var (from, to) = Span();

        _from = from;
        _to = to;

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
        MarkSelected();
        Raise(nameof(Rows), nameof(IsEmpty), nameof(From), nameof(To), nameof(HeaderText),
            nameof(TodayRow));
    }

    /// <summary>
    /// 出す範囲。持っているデータの端から端まで。
    /// <para>1件も無ければ、今日のまわりだけ出す。</para>
    /// </summary>
    private (DateOnly From, DateOnly To) Span()
    {
        var dates = _workspace.Events.All()
            .Where(e => !CalendarWorkspace.IsMilestoneMark(e))
            .SelectMany(e => new[] { e.Date, e.LastDate })
            .Concat(_workspace.Tasks.All().Where(t => t.Due is not null).Select(t => t.Due!.Value))
            .ToArray();

        if (dates.Length == 0)
        {
            return (_today.AddDays(-EmptySpanDays), _today.AddDays(EmptySpanDays));
        }

        // 今日が範囲の外にあっても、今日の行は出す。「いまどこにいるのか」が
        // 分からないと、上下どちらへ送ればよいのか決められない
        var first = dates.Min();
        var last = dates.Max();

        if (first > _today) first = _today;
        if (last < _today) last = _today;

        // 遠い未来や過去に紛れ込んだ1件で、日をなぞる処理が延々と回るのを止める
        var floor = _today.AddYears(-MaxYears);
        var ceiling = _today.AddYears(MaxYears);

        return (first < floor ? floor : first, last > ceiling ? ceiling : last);
    }

    private TaskListItemViewModel Row(TaskItem task) =>
        new(task,
            task.Due is { } due ? _workspace.DueFormatter.Format(due, _today) : null,
            // 済んだものには「2実働日 遅れて完了」を添える。右ペインと同じ出し方
            task is { IsDone: true, Due: { } d, CompletedAt: { } at }
                ? _workspace.DueFormatter.FormatDone(d, DateOnly.FromDateTime(at.LocalDateTime))
                : null);
}
