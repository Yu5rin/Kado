using System.Globalization;
using SlideinaCalendar.Core.WorkingDays;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>年ビューの出し方。</summary>
public enum YearLayout
{
    /// <summary>1行1か月、横に1〜31日を並べる。会社配布の実働日カレンダーと同じ形。</summary>
    Strip,

    /// <summary>ミニカレンダー12枚。日付を引き当てる用途。</summary>
    Grid,
}

/// <summary>
/// 年ストリップの1マス（1日）。
/// <para>
/// 出すのは曜日の頭文字と、稼働か休業かだけ。ここは<b>休みの並びと繁閑を縦に揃えて
/// 読む</b>ための面なので、予定は出さない。
/// </para>
/// </summary>
public sealed class YearDayViewModel
{
    internal YearDayViewModel(
        DateOnly date, bool hasData, bool isWorkingDay, bool isHoliday, bool isToday, bool isSelected)
    {
        Date = date;
        HasWorkingDayData = hasData;
        IsWorkingDay = isWorkingDay;
        IsHoliday = isHoliday;
        IsToday = isToday;
        IsSelected = isSelected;
    }

    public DateOnly Date { get; }

    public int DayNumber => Date.Day;

    /// <summary>曜日の頭文字。日付の上に添える。</summary>
    public string DayOfWeekMark => MonthStripViewModel.DayMarks[(int)Date.DayOfWeek];

    /// <summary>実働日データを持つ日か。持たない月は色を塗らない。</summary>
    public bool HasWorkingDayData { get; }

    public bool IsWorkingDay { get; }

    public bool IsHoliday { get; }

    public bool IsToday { get; }

    public bool IsSelected { get; }

    /// <summary>休業として塗る日か。データを持たないうちは塗らない。</summary>
    public bool IsOffDay => HasWorkingDayData && !IsWorkingDay;

    public bool IsSundayLike => Date.DayOfWeek == DayOfWeek.Sunday || IsHoliday;

    public bool IsSaturday => Date.DayOfWeek == DayOfWeek.Saturday;

    public string Tooltip
    {
        get
        {
            var day = Date.ToString("M月d日（ddd）", CultureInfo.GetCultureInfo("ja-JP"));

            if (IsHoliday) return $"{day} 祝日";
            if (!HasWorkingDayData) return day;

            return IsWorkingDay ? $"{day} 稼働" : $"{day} 休業";
        }
    }
}

/// <summary>
/// 年ストリップの1行（1か月）。
/// <para>
/// <b>月末より後ろには枠を作らない。</b>2月なら28日で打ち切る。空の枠を置くと、
/// 月によって右端がぶれて縦の並びが読みにくくなる（要件書 5.1）。
/// </para>
/// </summary>
public sealed class MonthStripViewModel
{
    /// <summary>曜日の頭文字。日曜から。</summary>
    internal static readonly string[] DayMarks = ["日", "月", "火", "水", "木", "金", "土"];

    internal MonthStripViewModel(int year, int month, IReadOnlyList<YearDayViewModel> days, int? workingDays)
    {
        Year = year;
        Month = month;
        Days = days;
        WorkingDayCount = workingDays;
    }

    public int Year { get; }

    public int Month { get; }

    /// <summary>「4月」。年度をまたぐので、年は行には出さない（左のまとまりで示す）。</summary>
    public string Label => $"{Month}月";

    /// <summary>1日から月末まで。</summary>
    public IReadOnlyList<YearDayViewModel> Days { get; }

    /// <summary>右端に出す実働日数。データが揃っていない月は null。</summary>
    public int? WorkingDayCount { get; }

    /// <summary>右端に出す文字。揃っていなければ「−」。</summary>
    public string WorkingDayCountText => WorkingDayCount?.ToString(CultureInfo.InvariantCulture) ?? "−";

    /// <summary>この月が今日を含むか。行ごと目立たせる。</summary>
    public bool HasToday => Days.Any(d => d.IsToday);
}

/// <summary>
/// 年ビュー（要件書 5.1）。
/// <para>
/// <b>年度単位（4月〜翌3月）。</b>会社の実働日カレンダーが年度で配られるので、暦年で
/// 区切ると配布物と突き合わせられない。上期（4〜9月）と下期（10〜3月）は行のまとまりで
/// 示し、半期ビューは設けない。
/// </para>
/// <para>
/// ストリップとカレンダーの切り替えは<b>このビューの中のボタン</b>で行う。年ビューを
/// 見ているときにしか関係しない選び方なので、設定画面には出さない（要件書 5.1）。
/// </para>
/// </summary>
public sealed class YearViewModel : ObservableObject
{
    /// <summary>年度の始まり。4月。</summary>
    public const int FiscalStartMonth = 4;

    private readonly CalendarWorkspace _workspace;
    private readonly DateOnly _today;

    private int _fiscalYear;
    private YearLayout _layout = YearLayout.Strip;
    private DateOnly _selectedDate;

    public YearViewModel(CalendarWorkspace workspace, DateOnly today, YearLayout layout = YearLayout.Strip)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _today = today;
        _selectedDate = today;
        _layout = layout;
        _fiscalYear = FiscalYearOf(today);

        Refresh();
    }

    /// <summary>その日が属する年度。1〜3月は前の年の年度。</summary>
    public static int FiscalYearOf(DateOnly date) =>
        date.Month >= FiscalStartMonth ? date.Year : date.Year - 1;

    /// <summary>いま出している年度。</summary>
    public int FiscalYear
    {
        get => _fiscalYear;
        set
        {
            if (!Set(ref _fiscalYear, value)) return;

            Refresh();
            Raise(nameof(HeaderText));
        }
    }

    /// <summary>「2026年度（2026年4月〜2027年3月）」。</summary>
    public string HeaderText => $"{_fiscalYear}年度（{_fiscalYear}年4月〜{_fiscalYear + 1}年3月）";

    /// <summary>出し方。切り替えたら覚える（持ち主が設定へ控える）。</summary>
    public YearLayout Layout
    {
        get => _layout;
        set
        {
            if (!Set(ref _layout, value)) return;

            Raise(nameof(IsStrip), nameof(IsGrid));
            LayoutChanged?.Invoke(this, value);
        }
    }

    /// <summary>出し方が変わったとき。設定に控えるのは持ち主の仕事。</summary>
    public event EventHandler<YearLayout>? LayoutChanged;

    public bool IsStrip => _layout == YearLayout.Strip;

    public bool IsGrid => _layout == YearLayout.Grid;

    /// <summary>選んでいる日。マスを押すと動く。</summary>
    public DateOnly SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (_selectedDate == value) return;

            _selectedDate = value;

            // 年度の外を選んだら、その年度へ移る
            var fiscal = FiscalYearOf(value);
            if (fiscal != _fiscalYear)
            {
                FiscalYear = fiscal;
            }
            else
            {
                Refresh();
            }

            Raise(nameof(SelectedDate));
        }
    }

    /// <summary>上期（4〜9月）。</summary>
    public IReadOnlyList<MonthStripViewModel> FirstHalf { get; private set; } = [];

    /// <summary>下期（10〜3月）。</summary>
    public IReadOnlyList<MonthStripViewModel> SecondHalf { get; private set; } = [];

    /// <summary>12か月ぶん。カレンダー表示で使う。</summary>
    public IReadOnlyList<MonthStripViewModel> Months { get; private set; } = [];

    /// <summary>年度を通した実働日数。揃っていない月があれば null。</summary>
    public int? WorkingDayTotal { get; private set; }

    /// <summary>右上に出す文字。</summary>
    public string WorkingDayTotalText =>
        WorkingDayTotal is { } total ? $"年度の実働 {total} 日" : "実働日データが揃っていません";

    /// <summary>前の年度へ。</summary>
    public void GoToPreviousYear() => FiscalYear--;

    /// <summary>次の年度へ。</summary>
    public void GoToNextYear() => FiscalYear++;

    /// <summary>今日を含む年度へ。</summary>
    public void GoToToday() => FiscalYear = FiscalYearOf(_today);

    /// <summary>その日を含む年度へ移す。</summary>
    public void GoTo(DateOnly date) => FiscalYear = FiscalYearOf(date);

    /// <summary>データを読み直して組み直す。</summary>
    public void Refresh()
    {
        var workingDays = _workspace.WorkingDays;
        var months = new List<MonthStripViewModel>(12);

        for (var i = 0; i < 12; i++)
        {
            var month = new DateOnly(_fiscalYear, FiscalStartMonth, 1).AddMonths(i);

            months.Add(BuildMonth(month.Year, month.Month, workingDays));
        }

        Months = months;
        FirstHalf = months.Take(6).ToArray();
        SecondHalf = months.Skip(6).ToArray();

        // 1か月でも揃っていなければ合計は出さない。足りないまま足すと、
        // 本当より少ない数を正しい数として読んでしまう
        WorkingDayTotal = months.All(m => m.WorkingDayCount is not null)
            ? months.Sum(m => m.WorkingDayCount!.Value)
            : null;

        Raise(nameof(FirstHalf), nameof(SecondHalf), nameof(Months),
            nameof(WorkingDayTotal), nameof(WorkingDayTotalText));
    }

    private MonthStripViewModel BuildMonth(int year, int month, WorkingDayCalendar workingDays)
    {
        var last = DateTime.DaysInMonth(year, month);
        var days = new List<YearDayViewModel>(last);

        for (var day = 1; day <= last; day++)
        {
            var date = new DateOnly(year, month, day);

            days.Add(new YearDayViewModel(
                date,
                workingDays.HasDataFor(date),
                workingDays.IsWorkingDay(date),
                _workspace.Holidays.NameOf(date) is not null,
                date == _today,
                date == _selectedDate));
        }

        // 揃っていない月に数を出すと、本当より少ない数を正しい数として読んでしまう
        var count = workingDays.IsMonthFullyCovered(year, month)
            ? workingDays.CountInMonth(year, month)
            : (int?)null;

        return new MonthStripViewModel(year, month, days, count);
    }
}
