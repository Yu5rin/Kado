using System.Globalization;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>
/// 日ビュー（要件書 5.1）。
/// <para>
/// 週ビューの列を1本だけ出したもの。時間軸の組み立ては
/// <see cref="TimelineBuilder"/> と共有しているので、置き場所の規則は同じ。
/// </para>
/// </summary>
public sealed class DayViewModel : ObservableObject
{
    private static readonly string[] JapaneseDayNames = ["日", "月", "火", "水", "木", "金", "土"];

    private readonly CalendarWorkspace _workspace;
    private TimelineBuilder _timeline;

    private DateOnly _date;
    private DateOnly _today;
    private WeekDayColumnViewModel _day;

    public DayViewModel(CalendarWorkspace workspace, DateOnly date, DateOnly today,
        ICalendarSources? sources = null, TimeOnly? dayStart = null, TimeOnly? dayEnd = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        // 日ビューは列が1本なので、モックどおり1時間を高く取る
        _timeline = new TimelineBuilder(workspace, sources, dayStart, dayEnd, TimelineBuilder.DayHourHeight);
        _date = date;
        _today = today;
        _day = _timeline.Build(date, date, today)[0];
    }

    /// <summary>表示している日。</summary>
    public DateOnly Date
    {
        get => _date;
        set
        {
            if (Set(ref _date, value)) Refresh();
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

    /// <summary>その日の中身。週ビューの1列と同じもの。</summary>
    public WeekDayColumnViewModel Day
    {
        get => _day;
        private set => Set(ref _day, value);
    }

    /// <summary>「9月24日（木）」。</summary>
    public string Title =>
        $"{_date.ToString("M月d日", CultureInfo.InvariantCulture)}（{JapaneseDayNames[(int)_date.DayOfWeek]}）";

    /// <summary>
    /// 「実働 17日目」。日ビューの見出しも通し番号を出す場所（要件書 4.3）。
    /// 非稼働日とデータ範囲外は null。
    /// </summary>
    public string? WorkingDayLabel => _day.WorkingDayLabel;

    /// <summary>
    /// 「月末まで 5実働日」。表示している日を起点に数える。
    /// <para>右ペインは今日を起点にするが、こちらは見ている日の先行きを知りたい。</para>
    /// </summary>
    public string? RemainingInMonthText =>
        _workspace.WorkingDays.IsMonthFullyCovered(_date.Year, _date.Month)
            ? $"月末まで {_workspace.WorkingDays.RemainingInMonth(_date)}実働日"
            : null;

    /// <summary>時間軸の左に出す「8:00」などの見出し。</summary>
    public IReadOnlyList<string> HourLabels => _timeline.HourLabels;

    /// <summary>1時間分の高さ。</summary>
    public double HourHeight => _timeline.HourHeight;

    /// <summary>時間軸全体の高さ。</summary>
    public double TimelineHeight => _timeline.TimelineHeight;

    /// <summary>現在時刻の線を出すか。今日を表示していて、表示時間帯に入っているときだけ。</summary>
    public bool ShowNowLine { get; private set; }

    /// <summary>現在時刻の線の位置。</summary>
    public double NowOffset { get; private set; }

    public void GoToPreviousDay() => Date = _date.AddDays(-1);

    public void GoToNextDay() => Date = _date.AddDays(1);

    public void GoToToday() => Date = _today;

    /// <summary>現在時刻の線を動かす。</summary>
    public void UpdateNowLine(TimeOnly now)
    {
        var inRange = _timeline.Covers(now);

        ShowNowLine = inRange && _date == _today;
        NowOffset = inRange ? _timeline.OffsetOf(now) : 0;

        Raise(nameof(ShowNowLine), nameof(NowOffset));
    }

    /// <summary>読み直す。</summary>
    /// <inheritdoc cref="WeekViewModel.ViewportHeight"/>
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

    public void Refresh()
    {
        Day = _timeline.Build(_date, _date, _today)[0];

        Raise(nameof(Title), nameof(WorkingDayLabel), nameof(RemainingInMonthText),
              nameof(HourLabels), nameof(HourHeight), nameof(TimelineHeight));
    }
}
