using System.Globalization;
using SlideinaCalendar.Core.WorkingDays;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Data.Repositories;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>
/// 時間軸に置く1件。予定と作業時間ブロックの両方を同じ形で扱う。
/// <para>
/// 位置は分ではなく<b>高さそのもの</b>で持つ。モックの1時間 44px に対して
/// 開始・長さを何度も掛け算するより、置き場所を1か所で決めるほうが崩れにくい。
/// </para>
/// </summary>
public sealed class TimeBlockViewModel
{
    internal TimeBlockViewModel(string id, string title, TimeOnly start, TimeOnly end,
        double top, double height, string? color, bool isWorkBlock, string? location,
        string? taskId = null)
    {
        Id = id;
        TaskId = taskId;
        Title = title;
        Start = start;
        End = end;
        Top = top;
        Height = height;
        Color = color;
        IsWorkBlock = isWorkBlock;
        Location = location;
    }

    public string Id { get; }

    public string Title { get; }

    public TimeOnly Start { get; }

    public TimeOnly End { get; }

    /// <summary>時間軸の上端からの位置。</summary>
    public double Top { get; }

    /// <summary>高さ。下の罫線と重ならないよう少し詰めてある。</summary>
    public double Height { get; }

    /// <summary>帯の色（<c>#rrggbb</c>）。所属カレンダーで決まる。null なら既定のアクセント色。</summary>
    public string? Color { get; }

    /// <summary>
    /// タスクの作業時間ブロックか。
    /// <para>予定には変換しないので、点線枠で見分けられるようにする（要件書 5.4）。</para>
    /// </summary>
    public bool IsWorkBlock { get; }

    /// <summary>
    /// 作業時間ブロックなら、そのもとになったタスク。予定なら null。
    /// <para>ブロック自身の識別子とは別物。押したときに開くのはタスクのほう。</para>
    /// </summary>
    public string? TaskId { get; }

    public string? Location { get; }

    /// <summary>「09:00」。ブロックの頭に出す。</summary>
    public string TimeText => Start.ToString("HH:mm", CultureInfo.InvariantCulture);

    public string Tooltip => Location is { Length: > 0 }
        ? $"{TimeText}–{End:HH:mm} {Title}（{Location}）"
        : $"{TimeText}–{End:HH:mm} {Title}";
}

/// <summary>
/// 週ビューの1日分の列。
/// <para>日ビューはこれを1本だけ並べたものとして扱う。</para>
/// </summary>
public sealed class WeekDayColumnViewModel
{
    private static readonly string[] JapaneseDayNames = ["日", "月", "火", "水", "木", "金", "土"];

    internal WeekDayColumnViewModel(DateOnly date, DateOnly today, WorkingDayCalendar workingDays,
        string? holidayName, IReadOnlyList<ScheduledEvent> allDay, IReadOnlyList<TaskItem> tasks,
        IReadOnlyList<TimeBlockViewModel> blocks, ICalendarPalette palette,
        IReadOnlyList<MilestoneViewModel>? milestones = null)
    {
        Date = date;
        IsToday = date == today;
        HolidayName = holidayName;
        AllDayEvents = allDay
            .Select(e => new EventChipViewModel(e, palette.ColorOf(e.Source.CalendarId)))
            .ToArray();
        Tasks = tasks;
        Blocks = blocks;

        HasWorkingDayData = workingDays.HasDataFor(date);
        IsWorkingDay = workingDays.IsWorkingDay(date);
        Milestones = milestones ?? [];
        WorkingDayIndex = workingDays.IndexInMonth(date);
    }

    public DateOnly Date { get; }

    public int DayNumber => Date.Day;

    /// <summary>「木」。祝日名があれば見出しに続けて出す。</summary>
    public string DayName => JapaneseDayNames[(int)Date.DayOfWeek];

    /// <summary>「木 敬老の日」。祝日でなければ曜日だけ。</summary>
    public string HeaderText => HolidayName is { Length: > 0 } name ? $"{DayName} {name}" : DayName;

    public string? HolidayName { get; }

    public bool IsToday { get; }

    public bool IsSunday => Date.DayOfWeek == DayOfWeek.Sunday;

    /// <inheritdoc cref="DayCellViewModel.IsSundayLike"/>
    public bool IsSundayLike => IsSunday || HolidayName is { Length: > 0 };

    public bool IsSaturday => Date.DayOfWeek == DayOfWeek.Saturday;

    public bool HasWorkingDayData { get; }

    public bool IsWorkingDay { get; }

    /// <summary>非稼働日は面を沈める。データ範囲外は判断できないので沈めない。</summary>
    public bool IsDimmed => HasWorkingDayData && !IsWorkingDay;

    /// <inheritdoc cref="DayCellViewModel.IsWorkingDayLit"/>
    public bool IsWorkingDayLit => HasWorkingDayData && IsWorkingDay;

    public IReadOnlyList<MilestoneViewModel> Milestones { get; }

    /// <summary>実働日の月内通し番号。</summary>
    public int? WorkingDayIndex { get; }

    /// <summary>
    /// 「実働 15日目」。週ビューの日付ヘッダは通し番号を出す場所のひとつ（要件書 4.3）。
    /// 非稼働日とデータ範囲外は null。
    /// </summary>
    public string? WorkingDayLabel => WorkingDayIndex is { } index ? $"実働 {index}日目" : null;

    /// <summary>終日レーンに並べる予定。</summary>
    public IReadOnlyList<EventChipViewModel> AllDayEvents { get; }

    /// <summary>終日レーンに並べる期限付きタスク。ここから時間帯へドラッグする（要件書 5.4）。</summary>
    public IReadOnlyList<TaskItem> Tasks { get; }

    /// <summary>時間軸に置く予定と作業時間ブロック。</summary>
    public IReadOnlyList<TimeBlockViewModel> Blocks { get; }

    /// <summary>終日レーンに何か入っているか。</summary>
    public bool HasAllDayItems => AllDayEvents.Count > 0 || Tasks.Count > 0;
}

/// <summary>
/// 週ビュー（要件書 5.1）。
/// <para>
/// 上に終日レーン、下に時間軸。タスクを時間軸へ置くと作業時間ブロックになるが、
/// データとしてはタスクのまま扱う（要件書 5.4）。
/// </para>
/// </summary>
public sealed class WeekViewModel : ObservableObject
{
    private TimelineBuilder _timeline;
    private readonly DayOfWeek _weekStart;

    private DateOnly _anchor;
    private DateOnly _today;
    private IReadOnlyList<WeekDayColumnViewModel> _days = [];

    public WeekViewModel(CalendarWorkspace workspace, DateOnly anchor, DateOnly today,
        DayOfWeek weekStart = DayOfWeek.Sunday, ICalendarSources? sources = null,
        TimeOnly? dayStart = null, TimeOnly? dayEnd = null)
    {
        _timeline = new TimelineBuilder(workspace, sources, dayStart, dayEnd);
        _weekStart = weekStart;
        _anchor = anchor;
        _today = today;

        Refresh();
    }

    /// <summary>週の頭の日。</summary>
    public DateOnly WeekStart => _anchor.AddDays(-(((int)_anchor.DayOfWeek - (int)_weekStart + 7) % 7));

    /// <summary>週の最後の日。</summary>
    public DateOnly WeekEnd => WeekStart.AddDays(6);

    /// <summary>「2026年9月20日 〜 26日」。月をまたぐときは両方に月を付ける。</summary>
    public string Title
    {
        get
        {
            var (from, to) = (WeekStart, WeekEnd);

            return from.Month == to.Month
                ? $"{from.ToString("yyyy年M月d日", CultureInfo.InvariantCulture)} 〜 {to.Day}日"
                : $"{from.ToString("yyyy年M月d日", CultureInfo.InvariantCulture)} 〜 " +
                  $"{to.ToString("M月d日", CultureInfo.InvariantCulture)}";
        }
    }

    /// <summary>今日。</summary>
    public DateOnly Today
    {
        get => _today;
        set
        {
            if (Set(ref _today, value)) Refresh();
        }
    }

    /// <summary>並べる7日分。</summary>
    public IReadOnlyList<WeekDayColumnViewModel> Days
    {
        get => _days;
        private set => Set(ref _days, value);
    }

    /// <summary>時間軸の左に出す「8:00」などの見出し。</summary>
    public IReadOnlyList<string> HourLabels => _timeline.HourLabels;

    /// <summary>1時間分の高さ。罫線の間隔もこれで決まる。</summary>
    public double HourHeight => _timeline.HourHeight;

    /// <summary>時間軸全体の高さ。</summary>
    public double TimelineHeight => _timeline.TimelineHeight;

    /// <summary>現在時刻の線を出すか。今日がこの週にあり、表示時間帯に入っているときだけ。</summary>
    public bool ShowNowLine { get; private set; }

    /// <summary>現在時刻の線の位置。</summary>
    public double NowOffset { get; private set; }

    /// <summary>表示する週を変える。</summary>
    public void GoTo(DateOnly date)
    {
        if (Set(ref _anchor, date, nameof(WeekStart))) Refresh();
    }

    public void GoToPreviousWeek() => GoTo(_anchor.AddDays(-7));

    public void GoToNextWeek() => GoTo(_anchor.AddDays(7));

    public void GoToToday() => GoTo(_today);

    /// <summary>現在時刻の線を動かす。</summary>
    public void UpdateNowLine(TimeOnly now)
    {
        var inRange = _timeline.Covers(now);

        ShowNowLine = inRange && _today >= WeekStart && _today <= WeekEnd;
        NowOffset = inRange ? _timeline.OffsetOf(now) : 0;

        Raise(nameof(ShowNowLine), nameof(NowOffset));
    }

    /// <summary>
    /// 時間軸に使える高さ。表示側が測って渡す。
    /// <para>選んだ時間帯をこの高さに割り付ける。入りきらなければスクロールになる。</para>
    /// </summary>
    public double ViewportHeight
    {
        set
        {
            if (double.IsNaN(value) || value <= 0) return;

            var height = _timeline.HourHeightFor(value);
            if (Math.Abs(height - _timeline.HourHeight) < 0.5) return;

            _timeline = _timeline.WithHourHeight(height);
            Refresh();
        }
    }

    /// <summary>データを読み直して列を組み直す。</summary>
    public void Refresh()
    {
        Days = _timeline.Build(WeekStart, WeekEnd, _today);

        Raise(nameof(Title), nameof(WeekStart), nameof(WeekEnd),
              nameof(HourLabels), nameof(HourHeight), nameof(TimelineHeight));
    }
}
