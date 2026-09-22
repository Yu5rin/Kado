using System.Globalization;
using Kado.Presentation.Infrastructure;

namespace Kado.Presentation.ViewModels;

/// <summary>
/// ミニ月暦の1マス。
/// <para>
/// 出すのは日付だけ。予定の有無も実働日の番号も出さない。中央の月ビューと同じ情報を
/// 小さく繰り返しても読めず、ここは「どの月のどのあたりか」を掴むための地図に徹する。
/// </para>
/// </summary>
public sealed class MiniDayViewModel : ObservableObject
{
    private bool _isSelected;

    internal MiniDayViewModel(DateOnly date, bool isCurrentMonth, bool isToday, bool isHoliday)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
        IsToday = isToday;
        IsHoliday = isHoliday;
    }

    public DateOnly Date { get; }

    public int DayNumber => Date.Day;

    /// <summary>表示している月の日か。前後の月は薄くする。</summary>
    public bool IsCurrentMonth { get; }

    public bool IsToday { get; }

    /// <summary>祝日か。日曜と同じ色で出す。</summary>
    public bool IsHoliday { get; }

    /// <summary>日曜または祝日。赤で出す条件。</summary>
    public bool IsSundayLike => Date.DayOfWeek == DayOfWeek.Sunday || IsHoliday;

    public bool IsSaturday => Date.DayOfWeek == DayOfWeek.Saturday;

    /// <summary>選択中か。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

/// <summary>
/// 左パネルのミニ月暦（要件書 5.2）。
/// <para>
/// 中央の月ビューとは独立して月を送れる。「来月のあの日」を中央の表示を崩さずに
/// 探せるようにするため。日を押したときだけ中央と選択を合わせる。
/// </para>
/// </summary>
public sealed class MiniCalendarViewModel : ObservableObject
{
    private static readonly string[] JapaneseDayNames = ["日", "月", "火", "水", "木", "金", "土"];

    private readonly CalendarWorkspace _workspace;
    private readonly DayOfWeek _weekStart;

    private DateOnly _month;
    private DateOnly _today;
    private DateOnly? _selectedDate;
    private IReadOnlyList<MiniDayViewModel> _days = [];

    public MiniCalendarViewModel(CalendarWorkspace workspace, DateOnly month, DateOnly today,
        DayOfWeek weekStart = DayOfWeek.Sunday)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _weekStart = weekStart;
        _month = new DateOnly(month.Year, month.Month, 1);
        _today = today;

        WeekDayHeaders = Enumerable.Range(0, 7)
            .Select(i => (DayOfWeek)(((int)weekStart + i) % 7))
            .Select(d => new WeekDayHeader(JapaneseDayNames[(int)d], d))
            .ToArray();

        Refresh();
    }

    /// <summary>「2026年9月」。</summary>
    public string Title => _month.ToString("yyyy年M月", CultureInfo.InvariantCulture);

    /// <summary>曜日の見出し。</summary>
    public IReadOnlyList<WeekDayHeader> WeekDayHeaders { get; }

    /// <summary>並べるマス。</summary>
    public IReadOnlyList<MiniDayViewModel> Days
    {
        get => _days;
        private set => Set(ref _days, value);
    }

    /// <summary>表示している月（その月の1日）。</summary>
    public DateOnly Month => _month;

    /// <summary>今日。</summary>
    public DateOnly Today
    {
        get => _today;
        set
        {
            if (Set(ref _today, value)) Refresh();
        }
    }

    /// <summary>選択中の日。中央のビューと共有する。</summary>
    public DateOnly? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (!Set(ref _selectedDate, value)) return;

            foreach (var day in _days) day.IsSelected = day.Date == value;
        }
    }

    /// <summary>表示する月を変える。</summary>
    public void GoTo(DateOnly month)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        if (_month == first) return;

        _month = first;
        Raise(nameof(Month), nameof(Title));
        Refresh();
    }

    public void GoToPreviousMonth() => GoTo(_month.AddMonths(-1));

    public void GoToNextMonth() => GoTo(_month.AddMonths(1));

    /// <summary>マスを組み直す。</summary>
    public void Refresh()
    {
        var monthEnd = _month.AddMonths(1).AddDays(-1);
        var leading = ((int)_month.DayOfWeek - (int)_weekStart + 7) % 7;
        var trailing = ((int)_weekStart + 6 - (int)monthEnd.DayOfWeek + 7) % 7;

        var from = _month.AddDays(-leading);
        var to = monthEnd.AddDays(trailing);

        var days = new List<MiniDayViewModel>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            days.Add(new MiniDayViewModel(
                date,
                isCurrentMonth: date.Month == _month.Month && date.Year == _month.Year,
                isToday: date == _today,
                isHoliday: _workspace.Holidays.NameOf(date) is not null)
            {
                IsSelected = date == _selectedDate,
            });
        }

        Days = days;
    }
}
