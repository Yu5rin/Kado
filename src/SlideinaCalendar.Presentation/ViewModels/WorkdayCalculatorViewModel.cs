using System.Globalization;
using SlideinaCalendar.Core.WorkingDays;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>
/// 実働日計算パネル（要件書 4.5）。
/// <para>
/// 2つだけ。期間から実働日数を出すのと、基準日から N 実働日進んだ日を出すの。
/// 常設はしない。ボタンで開いて、使ったら閉じる。
/// </para>
/// <para>
/// <b>ここは設定の「暦日で数える」に従わない。</b>暦日で数えるなら、この道具そのものが
/// 要らない。実働日で数えたいときに開くものなので、常に実働日で数える（要件書 4.5）。
/// </para>
/// </summary>
public sealed class WorkdayCalculatorViewModel : ObservableObject
{
    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");

    private readonly WorkingDayMath _math;

    private DateOnly _rangeFrom;
    private DateOnly _rangeTo;
    private DateOnly _baseDate;
    private int _offset = 10;

    public WorkdayCalculatorViewModel(WorkingDayMath math, DateOnly today)
    {
        _math = math ?? throw new ArgumentNullException(nameof(math));

        _rangeFrom = today;
        _rangeTo = today;
        _baseDate = today;
    }

    /// <summary>実働日データを持っているか。無ければ出しても何も出せない。</summary>
    public bool HasData => _math.Calendar.Count > 0;

    /// <summary>取り込んである範囲。「2026/4/1 〜 2027/3/31」。無ければ null。</summary>
    public string? CoverageText =>
        _math.Calendar is { RangeStart: { } start, RangeEnd: { } end }
            ? $"{start.ToString("yyyy/M/d", CultureInfo.InvariantCulture)} 〜 " +
              $"{end.ToString("yyyy/M/d", CultureInfo.InvariantCulture)}"
            : null;

    // ------------------------------------------------------------------
    // 期間 → 実働日数
    // ------------------------------------------------------------------

    /// <summary>数え始める日。</summary>
    public DateOnly RangeFrom
    {
        get => _rangeFrom;
        set
        {
            if (!Set(ref _rangeFrom, value)) return;

            // 逆に入れられたら、終わりを始まりに寄せる。負の日数を出しても読めない
            if (_rangeTo < _rangeFrom) Set(ref _rangeTo, _rangeFrom, nameof(RangeTo));

            RaiseRange();
        }
    }

    /// <summary>数え終わる日。この日を含む。</summary>
    public DateOnly RangeTo
    {
        get => _rangeTo;
        set
        {
            if (!Set(ref _rangeTo, value)) return;

            if (_rangeTo < _rangeFrom) Set(ref _rangeFrom, _rangeTo, nameof(RangeFrom));

            RaiseRange();
        }
    }

    /// <summary>
    /// 期間の実働日数。両端を含む。
    /// <para>
    /// <see cref="WorkingDayMath.CountBetween"/> は始まりを含まない（後ろ側だけ数える）。
    /// 人が「4/1 から 4/10 まで何日働くか」と聞くときは<b>両端を含む</b>ので、
    /// 始まりが稼働日なら1足す。
    /// </para>
    /// </summary>
    public int? RangeCount
    {
        get
        {
            if (_math.CountBetween(_rangeFrom, _rangeTo) is not { } exclusive) return null;

            return exclusive + (_math.Calendar.IsWorkingDay(_rangeFrom) ? 1 : 0);
        }
    }

    /// <summary>期間の暦日数。両端を含む。</summary>
    public int RangeCalendarDays => _rangeTo.DayNumber - _rangeFrom.DayNumber + 1;

    /// <summary>「12 実働日」。範囲の外なら断り書き。</summary>
    public string RangeResultText => RangeCount is { } count
        ? $"{count} 実働日"
        : "実働日データの範囲外です";

    /// <summary>「暦日では 14 日（うち休業 2 日）」。</summary>
    public string RangeDetailText
    {
        get
        {
            var calendar = RangeCalendarDays;

            if (RangeCount is not { } count) return $"暦日では {calendar} 日";

            return $"暦日では {calendar} 日（うち休業 {calendar - count} 日）";
        }
    }

    /// <summary>数えられたか。出せないときは結果を薄くする。</summary>
    public bool HasRangeResult => RangeCount is not null;

    // ------------------------------------------------------------------
    // 基準日 ＋ N 実働日 → 到達日
    // ------------------------------------------------------------------

    /// <summary>数え始める日。この日自体は数に入れない。</summary>
    public DateOnly BaseDate
    {
        get => _baseDate;
        set
        {
            if (!Set(ref _baseDate, value)) return;

            RaiseOffset();
        }
    }

    /// <summary>進める実働日数。負にすると遡る。</summary>
    public int Offset
    {
        get => _offset;
        set
        {
            if (!Set(ref _offset, value)) return;

            RaiseOffset();
        }
    }

    /// <summary>到達日。データが尽きたら null。</summary>
    public DateOnly? Arrival => _math.AddWorkingDays(_baseDate, _offset);

    /// <summary>「2026年10月9日（金）」。出せなければ断り書き。</summary>
    public string ArrivalResultText => Arrival is { } date
        ? date.ToString("yyyy年M月d日（ddd）", Japanese)
        : "実働日データが足りません";

    /// <summary>「基準日から暦日で 15 日後」。出せなければ null。</summary>
    public string? ArrivalDetailText
    {
        get
        {
            if (Arrival is not { } date) return null;

            var days = date.DayNumber - _baseDate.DayNumber;

            return days switch
            {
                0 => "基準日そのもの",
                > 0 => $"基準日から暦日で {days} 日後",
                _ => $"基準日から暦日で {-days} 日前",
            };
        }
    }

    /// <summary>出せたか。</summary>
    public bool HasArrival => Arrival is not null;

    /// <summary>
    /// カレンダー上でドラッグして選んだ期間を入れる（要件書 4.5）。
    /// <para>掴んだ向きは問わない。逆向きでも同じ期間として受ける。</para>
    /// </summary>
    public void SetRange(DateOnly from, DateOnly to)
    {
        if (to < from) (from, to) = (to, from);

        Set(ref _rangeFrom, from, nameof(RangeFrom));
        Set(ref _rangeTo, to, nameof(RangeTo));
        RaiseRange();
    }

    private void RaiseRange() => Raise(
        nameof(RangeCount), nameof(RangeCalendarDays),
        nameof(RangeResultText), nameof(RangeDetailText), nameof(HasRangeResult));

    private void RaiseOffset() => Raise(
        nameof(Arrival), nameof(ArrivalResultText), nameof(ArrivalDetailText), nameof(HasArrival));
}
