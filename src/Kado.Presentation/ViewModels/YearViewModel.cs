using System.Globalization;
using Kado.Data.Repositories;
using Kado.Core.WorkingDays;
using Kado.Presentation.Infrastructure;

namespace Kado.Presentation.ViewModels;

/// <summary>年ビューの出し方。</summary>
public enum YearLayout
{
    /// <summary>1行1か月、横に1〜31日を並べる。会社配布の実働日カレンダーと同じ形。</summary>
    Strip,

    /// <summary>ミニカレンダー12枚。日付を引き当てる用途。</summary>
    Grid,
}

/// <summary>
/// 年ビューのマスに重ねる印1件。
/// <para>
/// 仕様期限・1次GO のような日付の行の印は、<b>名前ごとに色が決まっている</b>
/// （月ビューと同じ決まり）。カレンダーの色で塗ると、Kado のものが
/// 全部同じ色になってしまい、何の区切りなのか分からない。
/// </para>
/// </summary>
/// <param name="Color">所属カレンダーの色（<c>#rrggbb</c>）。決まっていなければ null。</param>
/// <param name="MilestoneName">
/// 日付の行の印なら、その名前。ふつうの予定なら null。
/// <para>名前が入っていれば、表示側は名前から決まる色を使う。</para>
/// </param>
public sealed record DayMark(string? Color, string? MilestoneName);

/// <summary>目盛りの1マス。数字を置かない位置は空。</summary>
/// <param name="Label">出す数字。空なら何も出さない。</param>
public sealed record RulerMark(string Label);

/// <summary>
/// 年ストリップの1マス（1日）。
/// <para>
/// 出すのは曜日の頭文字と、稼働か休業かだけ。ここは<b>休みの並びと繁閑を縦に揃えて
/// 読む</b>ための面なので、予定は出さない。
/// </para>
/// </summary>
public sealed class YearDayViewModel : ObservableObject
{
    private bool _isSelected;

    internal YearDayViewModel(
        DateOnly date, bool hasData, bool isWorkingDay, bool isHoliday, bool isToday,
        IReadOnlyList<DayMark> topMarks, IReadOnlyList<DayMark> bottomMarks)
    {
        Date = date;
        HasWorkingDayData = hasData;
        IsWorkingDay = isWorkingDay;
        IsHoliday = isHoliday;
        IsToday = isToday;
        TopMarks = topMarks;
        BottomMarks = bottomMarks;
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

    /// <summary>
    /// 日付の<b>上</b>に重ねる色。「Kado」に入っているものだけ。
    /// <para>
    /// 仕様期限などの区切りは、自分の予定とは意味が違う。混ぜて並べると、どれが
    /// 会社の決めた日でどれが自分の用事なのか見分けられない。上下に分けて置く。
    /// </para>
    /// <para>null は色の決まっていないカレンダー。表示側が既定の色を使う。</para>
    /// </summary>
    public IReadOnlyList<DayMark> TopMarks { get; }

    /// <summary>日付の<b>下</b>に重ねる色。「Kado」以外の予定。</summary>
    public IReadOnlyList<DayMark> BottomMarks { get; }

    /// <summary>この日に入っている予定の数。</summary>
    public int MarkCount => TopMarks.Count + BottomMarks.Count;

    /// <summary>予定が入っているか。</summary>
    public bool HasEvents => MarkCount > 0;

    /// <summary>選んでいる日か。押すたびに動く。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    /// <summary>休業として塗る日か。データを持たないうちは塗らない。</summary>
    public bool IsOffDay => HasWorkingDayData && !IsWorkingDay;

    public bool IsSundayLike => Date.DayOfWeek == DayOfWeek.Sunday || IsHoliday;

    public bool IsSaturday => Date.DayOfWeek == DayOfWeek.Saturday;

    public string Tooltip
    {
        get
        {
            var day = Date.ToString("M月d日（ddd）", CultureInfo.GetCultureInfo("ja-JP"));

            var state = IsHoliday ? "祝日"
                : !HasWorkingDayData ? null
                : IsWorkingDay ? "稼働" : "休業";

            var events = MarkCount > 0 ? $"予定 {MarkCount} 件" : null;

            var parts = new[] { day, state, events }.Where(x => x is { Length: > 0 });

            return string.Join(" ・ ", parts);
        }
    }
}

/// <summary>
/// 年ストリップの1行（1か月）。
/// <para>
/// <b>月末より後ろには枠を作らない。</b>2月なら28日で打ち切る。空の枠を置くと、
/// 月によって右端がぶれて縦の並びが読みにくくなる（要件書 5.1）。ストリップは
/// 曜日で列を揃えないので、<see cref="Days"/> はこのままでよい。
/// </para>
/// </summary>
public sealed class MonthStripViewModel
{
    /// <summary>曜日の頭文字。日曜から。</summary>
    internal static readonly string[] DayMarks = ["日", "月", "火", "水", "木", "金", "土"];

    internal MonthStripViewModel(
        int year, int month, IReadOnlyList<YearDayViewModel> days,
        IReadOnlyList<YearDayViewModel?> gridDays, int? workingDays)
    {
        Year = year;
        Month = month;
        Days = days;
        GridDays = gridDays;
        WorkingDayCount = workingDays;
    }

    public int Year { get; }

    public int Month { get; }

    /// <summary>「4月」。年度をまたぐので、年は行には出さない（左のまとまりで示す）。</summary>
    public string Label => $"{Month}月";

    /// <summary>1日から月末まで。ストリップ表示用（曜日は揃えない）。</summary>
    public IReadOnlyList<YearDayViewModel> Days { get; }

    /// <summary>
    /// カレンダー表示（グリッド）用。
    /// <para>
    /// 月初を曜日の位置に置くための空きマス（<see langword="null"/>）を先頭に、
    /// 月末のあとは末尾に詰めて、<b>常に6行×7列＝42マス</b>にする。行数を月ごとに
    /// 変えると、12枚のカードで1マスの高さがまちまちになり縦の並びが崩れて見える
    /// ため、いちばん行数が多い月（6行）に揃えている。
    /// </para>
    /// </summary>
    public IReadOnlyList<YearDayViewModel?> GridDays { get; }

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

    /// <summary>1マスの幅の下げ止まり。これより細いと日付が読めない。</summary>
    public const double MinDayWidth = 15;

    /// <summary>
    /// 1マスの幅の上げ止まり。
    /// <para>
    /// 30 にしていたら、広い画面で右が大きく余った。会社配布のカレンダーと同じで、
    /// 横いっぱいに広げたほうが休みの並びを追いやすい。
    /// </para>
    /// </summary>
    public const double MaxDayWidth = 64;

    /// <summary>幅が分からないうちに使う幅。</summary>
    public const double DefaultDayWidth = 20;

    /// <summary>ストリップ1行の高さの下げ止まり。</summary>
    public const double MinDayHeight = 18;

    /// <summary>
    /// ストリップ1行の高さの上げ止まり。
    /// <para>12行しかないので、背の高い画面では行を伸ばして縦を使い切る。</para>
    /// </summary>
    public const double MaxDayHeight = 56;

    /// <summary>日付の上下それぞれに重ねる印の上限。増やすと日付が埋まる。</summary>
    private const int MaxMarksPerSide = 2;

    /// <summary>
    /// カレンダー表示（グリッド）の1か月あたりの行数。
    /// <para>
    /// 31日まである月が日曜始まりで土曜から始まると6行要る。行数を月ごとに
    /// 変えず、いちばん多い6行に揃えて全月の高さを合わせる。
    /// </para>
    /// </summary>
    public const int MonthGridRows = 6;

    /// <summary>曜日名。<see cref="DayOfWeek"/> の値をそのまま添字に使う。</summary>
    private static readonly string[] JapaneseDayNames = ["日", "月", "火", "水", "木", "金", "土"];

    private readonly CalendarWorkspace _workspace;
    private readonly ICalendarSources? _sources;
    private readonly DateOnly _today;
    private readonly DayOfWeek _weekStart;

    private int _fiscalYear;
    /// <summary>出し方の既定はカレンダー。会社で配るものと同じ形のほうが通りがいい。</summary>
    private YearLayout _layout = YearLayout.Grid;
    private DateOnly _selectedDate;
    private double _dayWidth = DefaultDayWidth;
    private double _dayHeight = DefaultDayWidth * 1.5;
    private int _gridColumns = 4;

    public YearViewModel(
        CalendarWorkspace workspace, DateOnly today, YearLayout layout = YearLayout.Grid,
        ICalendarSources? sources = null, DayOfWeek weekStart = DayOfWeek.Sunday)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _sources = sources;
        _today = today;
        _selectedDate = today;
        _layout = layout;
        _weekStart = weekStart;
        _fiscalYear = FiscalYearOf(today);

        // 見出しは週の開始曜日から順に回す。月ビューと同じ設定を見る（要件書 5.5）
        WeekDayHeaders = Enumerable.Range(0, 7)
            .Select(i => (DayOfWeek)(((int)weekStart + i) % 7))
            .Select(d => new WeekDayHeader(JapaneseDayNames[(int)d], d))
            .ToArray();

        SetLayoutCommand = new RelayCommand<object?>(value =>
        {
            if (value is YearLayout chosen) Layout = chosen;
            else if (value is string name && Enum.TryParse<YearLayout>(name, out var parsed)) Layout = parsed;
        });

        Refresh();
    }

    /// <summary>カレンダー表示の曜日見出し。週の開始曜日に合わせて回す。</summary>
    public IReadOnlyList<WeekDayHeader> WeekDayHeaders { get; }

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

    /// <summary>
    /// 出し方を切り替える。名前（文字列）でも受ける。
    /// <para>
    /// ボタンは <c>IsChecked</c> の双方向バインドをやめてこのコマンドで切り替える。
    /// RadioButton は仲間が選ばれたときに <c>IsChecked</c> を直に書き換えるので、
    /// そこで双方向のバインドが外れてしまう（WPF の癖）。
    /// </para>
    /// </summary>
    public RelayCommand<object?> SetLayoutCommand { get; }

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
                // 組み直さずに印だけ動かす。12か月ぶん作り直すと、押すたびに
                // 目に見えて詰まる
                MarkSelected();
            }

            Raise(nameof(SelectedDate));
        }
    }

    /// <summary>
    /// 1マスの幅。
    /// <para>
    /// 画面の幅から決める。固定にすると、広い画面では右が余り、狭い画面では
    /// 横のスクロールバーが出る。文字がつぶれない範囲で伸び縮みさせる。
    /// </para>
    /// </summary>
    public double DayWidth
    {
        get => _dayWidth;
        set
        {
            var width = double.IsNaN(value) || double.IsInfinity(value)
                ? DefaultDayWidth
                : Math.Clamp(value, MinDayWidth, MaxDayWidth);

            if (!Set(ref _dayWidth, width)) return;

            Raise(nameof(DayFontSize), nameof(MarkWidth));
        }
    }

    /// <summary>
    /// ストリップ1行の高さ。
    /// <para>
    /// 幅と同じく、入れ物の大きさから決める。幅の 1.5 倍で固定していたら、背の
    /// 高い画面で下が大きく余った。12行しか無いので、余ったぶんは行に配る。
    /// </para>
    /// </summary>
    public double DayHeight
    {
        get => _dayHeight;
        set
        {
            var height = double.IsNaN(value) || double.IsInfinity(value)
                ? DefaultDayWidth * 1.5
                : Math.Clamp(value, MinDayHeight, MaxDayHeight);

            Set(ref _dayHeight, height);
        }
    }

    /// <summary>
    /// 日付の文字の大きさ。細いマスでつぶれないよう、少し縮める。
    /// <para>
    /// 下限は 9.5。帯（設定の最小幅 160〜220px）ではマスが常にこの下限に当たるので、
    /// 読める大きさを割らないようにする。入り切らないぶんは既にある横スクロールに任せる。
    /// </para>
    /// </summary>
    public double DayFontSize => _dayWidth < 18 ? 9.5 : 10.5;

    /// <summary>予定の印の幅。マスより少し内側にする。</summary>
    public double MarkWidth => Math.Max(6, _dayWidth - 6);

    /// <summary>
    /// カレンダー表示の列数。
    /// <para>
    /// 既定は4列。左から縦に 4〜6月、7〜9月、10〜12月、1〜3月と並ぶ。四半期ごとに
    /// 縦に揃うので、期のまとまりが読める。画面が狭ければ表示側が減らす。
    /// </para>
    /// </summary>
    public int GridColumns
    {
        get => _gridColumns;
        set
        {
            var columns = Math.Clamp(value, 1, 6);

            if (!Set(ref _gridColumns, columns)) return;

            Raise(nameof(GridRows), nameof(GridMonths));
        }
    }

    /// <summary>
    /// 日付の目盛り。1・5・10・15・20・25・30 の位置にだけ数字を置く。
    /// <para>31 マスぶん並べるので、下の行と縦に揃う。</para>
    /// </summary>
    public IReadOnlyList<RulerMark> RulerMarks { get; } =
        Enumerable.Range(1, 31)
            .Select(d => new RulerMark(
                d is 1 or 5 or 10 or 15 or 20 or 25 or 30
                    ? d.ToString(CultureInfo.InvariantCulture)
                    : string.Empty))
            .ToArray();

    /// <summary>カレンダー表示の行数。12か月を列数で割る。</summary>
    public int GridRows => (int)Math.Ceiling(12.0 / _gridColumns);

    /// <summary>
    /// カレンダー表示に並べる順。
    /// <para>
    /// 入れ物は左から右へ詰めるので、<b>縦に読ませたいぶんだけ順番を入れ替える</b>。
    /// 4列なら 4月・7月・10月・1月、次の行が 5月・8月・11月・2月…となる。
    /// </para>
    /// </summary>
    public IReadOnlyList<MonthStripViewModel> GridMonths
    {
        get
        {
            var rows = GridRows;
            var ordered = new List<MonthStripViewModel>(Months.Count);

            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < _gridColumns; column++)
                {
                    var index = (column * rows) + row;

                    if (index < Months.Count) ordered.Add(Months[index]);
                }
            }

            return ordered;
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

    /// <summary>年度のうち、今日までに過ぎた実働日数。</summary>
    public int? WorkingDayElapsed { get; private set; }

    /// <summary>年度に残っている実働日数。</summary>
    public int? WorkingDayRemaining =>
        WorkingDayTotal is { } total && WorkingDayElapsed is { } done ? total - done : null;

    /// <summary>
    /// 見出しに添える数。「実働 243 日・経過 103 日・残り 140 日」。
    /// <para>年度のどのあたりに居るのかが、これだけで分かる。</para>
    /// </summary>
    public string WorkingDayTotalText
    {
        get
        {
            if (WorkingDayTotal is not { } total) return "実働日データが揃っていません";

            // 今年度でなければ、経過と残りを出しても意味がない
            if (WorkingDayElapsed is not { } done) return $"実働 {total} 日";

            return $"実働 {total} 日・経過 {done} 日・残り {total - done} 日";
        }
    }

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
        var from = new DateOnly(_fiscalYear, FiscalStartMonth, 1);
        var to = from.AddYears(1).AddDays(-1);

        // 1年ぶんまとめて引く。月ごとに引くと同じ表を12回なめることになる
        var eventsByDate = _workspace.Schedule.EventsByDate(from, to);

        // 「Kado」の予定は日付の上、それ以外は下に置く
        var workingDayCalendars = _workspace.WorkingDayCalendars()
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);

        var months = new List<MonthStripViewModel>(12);

        for (var i = 0; i < 12; i++)
        {
            var month = from.AddMonths(i);

            months.Add(BuildMonth(
                month.Year, month.Month, workingDays, eventsByDate, workingDayCalendars));
        }

        Months = months;
        FirstHalf = months.Take(6).ToArray();
        SecondHalf = months.Skip(6).ToArray();

        // 1か月でも揃っていなければ合計は出さない。足りないまま足すと、
        // 本当より少ない数を正しい数として読んでしまう
        WorkingDayTotal = months.All(m => m.WorkingDayCount is not null)
            ? months.Sum(m => m.WorkingDayCount!.Value)
            : null;

        // 経過は今年度だけ。過ぎた年度に「残り」を出しても読めない
        WorkingDayElapsed = WorkingDayTotal is not null && _fiscalYear == FiscalYearOf(_today)
            ? months.SelectMany(m => m.Days).Count(d => d.IsWorkingDay && d.Date <= _today)
            : null;

        MarkSelected();

        Raise(nameof(FirstHalf), nameof(SecondHalf), nameof(Months), nameof(GridMonths),
            nameof(WorkingDayTotal), nameof(WorkingDayElapsed), nameof(WorkingDayRemaining),
            nameof(WorkingDayTotalText));
    }

    /// <summary>どのマスが選ばれているかを反映する。</summary>
    private void MarkSelected()
    {
        foreach (var day in Months.SelectMany(m => m.Days))
        {
            day.IsSelected = day.Date == _selectedDate;
        }
    }

    private MonthStripViewModel BuildMonth(
        int year, int month, WorkingDayCalendar workingDays,
        IReadOnlyDictionary<DateOnly, IReadOnlyList<ScheduledEvent>> eventsByDate,
        IReadOnlySet<string> workingDayCalendars)
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
                MarksOn(date, eventsByDate, workingDayCalendars, inside: true),
                MarksOn(date, eventsByDate, workingDayCalendars, inside: false)));
        }

        // カレンダー表示（グリッド）用。月初を週の開始曜日の位置に置くため、
        // 先頭に空きマス（null）を挟む。週の始まりは設定に従う（要件書 5.5）
        var firstOfMonth = new DateOnly(year, month, 1);
        var leading = ((int)firstOfMonth.DayOfWeek - (int)_weekStart + 7) % 7;

        var gridDays = new List<YearDayViewModel?>(MonthGridRows * 7);
        gridDays.AddRange(Enumerable.Repeat((YearDayViewModel?)null, leading));
        gridDays.AddRange(days);

        // 末尾も6行×7列に揃うまで空きマスで埋める。前後の月の日は出さない
        // （曜日は分かるが、月をまたいだ予定と混ざって見えるのを避けるため）
        while (gridDays.Count < MonthGridRows * 7) gridDays.Add(null);

        // 揃っていない月に数を出すと、本当より少ない数を正しい数として読んでしまう
        var count = workingDays.IsMonthFullyCovered(year, month)
            ? workingDays.CountInMonth(year, month)
            : (int?)null;

        return new MonthStripViewModel(year, month, days, gridDays, count);
    }

    /// <summary>
    /// その日に重ねる印の色。
    /// <para>
    /// <paramref name="inside"/> が true なら「Kado」に入っているものだけ、
    /// false ならそれ以外。仕様期限などの区切りと自分の用事は意味が違うので、
    /// 日付の上下に分けて置く。
    /// </para>
    /// </summary>
    private IReadOnlyList<DayMark> MarksOn(
        DateOnly date,
        IReadOnlyDictionary<DateOnly, IReadOnlyList<ScheduledEvent>> eventsByDate,
        IReadOnlySet<string> workingDayCalendars,
        bool inside)
    {
        if (!eventsByDate.TryGetValue(date, out var scheduled)) return [];

        return scheduled
            .Where(e => IsWorkingDayCalendar(e.Source.CalendarId, workingDayCalendars) == inside)
            .Where(Shown)
            .Take(MaxMarksPerSide)
            // Kado のものは名前ごとに色を決める。仕様期限は黄、1次GO は赤…と、
            // 月ビューの日付の行と同じ色になる。カレンダーの色で塗ると全部同じ色に
            // なってしまい、何の区切りなのか分からない
            .Select(e => new DayMark(
                _sources?.ColorOf(e.Source.CalendarId),
                inside ? e.Source.Title : null))
            .ToArray();
    }

    /// <summary>
    /// 左パネルのチェックを通ったものか。
    /// <para>
    /// <c>IncludesEvent</c> はマイルストーンを外す。日付の行に出すものを予定の並びにも
    /// 出さないためだが、<b>年ビューには日付の行が無い</b>。外したままだと仕様期限が
    /// どこにも出ないので、こちらでは両方を見る。
    /// </para>
    /// </summary>
    private bool Shown(ScheduledEvent scheduled)
    {
        if (_sources is not { } sources) return true;

        return sources.IncludesEvent(scheduled.Source) || sources.IncludesMilestone(scheduled.Source);
    }

    private static bool IsWorkingDayCalendar(string? calendarId, IReadOnlySet<string> ids) =>
        calendarId is { Length: > 0 } id && ids.Contains(id);
}
