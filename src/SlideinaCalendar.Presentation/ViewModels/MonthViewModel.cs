using System.Globalization;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>
/// 月ビュー。
/// <para>
/// マスの数は週の数で決める。6 週に固定すると、4 週や 5 週で収まる月に空の行が出て
/// 間延びする。月によって高さが変わるが、そのほうが密度が揃う。
/// </para>
/// </summary>
public sealed class MonthViewModel : ObservableObject
{
    /// <summary>曜日名。<see cref="DayOfWeek"/> の値をそのまま添字に使う。</summary>
    private static readonly string[] JapaneseDayNames = ["日", "月", "火", "水", "木", "金", "土"];

    private readonly CalendarWorkspace _workspace;
    private readonly DayOfWeek _weekStart;

    private DateOnly _month;
    private DateOnly _today;
    private DateOnly? _selectedDate;
    private IReadOnlyList<DayCellViewModel> _cells = [];

    public MonthViewModel(CalendarWorkspace workspace, DateOnly month, DateOnly today,
        DayOfWeek weekStart = DayOfWeek.Sunday)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _weekStart = weekStart;
        _month = new DateOnly(month.Year, month.Month, 1);
        _today = today;

        // 見出しは週の開始曜日から順に回す。日曜始まりと月曜始まりを設定で選べる（要件書 5.5）
        WeekDayHeaders = Enumerable.Range(0, 7)
            .Select(i => JapaneseDayNames[((int)weekStart + i) % 7])
            .ToArray();

        Refresh();
    }

    /// <summary>表示している月（その月の1日）。</summary>
    public DateOnly Month => _month;

    /// <summary>今日。日付が変わったときに差し替える。</summary>
    public DateOnly Today
    {
        get => _today;
        set
        {
            if (Set(ref _today, value)) Refresh();
        }
    }

    /// <summary>選択されている日。</summary>
    public DateOnly? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (!Set(ref _selectedDate, value)) return;

            foreach (var cell in _cells) cell.IsSelected = cell.Date == value;
        }
    }

    /// <summary>並べるマス。左上から右下へ、週の数だけ。</summary>
    public IReadOnlyList<DayCellViewModel> Cells
    {
        get => _cells;
        private set => Set(ref _cells, value);
    }

    /// <summary>「2026年9月」のような見出し。</summary>
    public string Title => _month.ToString("yyyy年M月", CultureInfo.InvariantCulture);

    /// <summary>曜日の見出し。週の開始曜日に合わせて回す。</summary>
    public IReadOnlyList<string> WeekDayHeaders { get; }

    /// <summary>この月の実働日数。</summary>
    public int WorkingDayCount => _workspace.WorkingDays.CountInMonth(_month.Year, _month.Month);

    /// <summary>今日を含む月なら、今日以降の残り実働日数。違う月なら null。</summary>
    public int? RemainingWorkingDays =>
        _today.Year == _month.Year && _today.Month == _month.Month
            ? _workspace.WorkingDays.RemainingInMonth(_today)
            : null;

    /// <summary>実働日データがこの月を丸ごと覆っているか。部分的なら件数は参考値にとどまる。</summary>
    public bool HasFullWorkingDayData =>
        _workspace.WorkingDays.IsMonthFullyCovered(_month.Year, _month.Month);

    /// <summary>表示する月を変える。</summary>
    public void GoTo(DateOnly month)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        if (_month == first) return;

        _month = first;
        Raise(nameof(Month), nameof(Title));
        Refresh();
    }

    /// <summary>前の月へ。</summary>
    public void GoToPreviousMonth() => GoTo(_month.AddMonths(-1));

    /// <summary>次の月へ。</summary>
    public void GoToNextMonth() => GoTo(_month.AddMonths(1));

    /// <summary>今日を含む月へ。</summary>
    public void GoToToday()
    {
        GoTo(_today);
        SelectedDate = _today;
    }

    /// <summary>データを読み直してマスを組み直す。</summary>
    public void Refresh()
    {
        var (from, to) = VisibleRange();

        var eventsByDate = _workspace.Schedule.EventsByDate(from, to);
        var tasksByDue = _workspace.Schedule.TasksByDue(from, to);
        var workingDays = _workspace.WorkingDays;

        var cells = new List<DayCellViewModel>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            cells.Add(new DayCellViewModel(
                date,
                isCurrentMonth: date.Month == _month.Month && date.Year == _month.Year,
                _today,
                workingDays,
                eventsByDate.TryGetValue(date, out var events) ? events : [],
                tasksByDue.TryGetValue(date, out var tasks) ? tasks : []));
        }

        foreach (var cell in cells) cell.IsSelected = cell.Date == _selectedDate;

        Cells = cells;
        Raise(nameof(WorkingDayCount), nameof(RemainingWorkingDays), nameof(HasFullWorkingDayData));
    }

    /// <summary>マスに並べる期間。月初を含む週の頭から、月末を含む週の終わりまで。</summary>
    internal (DateOnly From, DateOnly To) VisibleRange()
    {
        var monthEnd = _month.AddMonths(1).AddDays(-1);

        var leading = ((int)_month.DayOfWeek - (int)_weekStart + 7) % 7;
        var trailing = ((int)_weekStart + 6 - (int)monthEnd.DayOfWeek + 7) % 7;

        return (_month.AddDays(-leading), monthEnd.AddDays(trailing));
    }
}
