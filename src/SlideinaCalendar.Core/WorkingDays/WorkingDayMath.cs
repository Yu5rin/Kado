namespace SlideinaCalendar.Core.WorkingDays;

/// <summary>
/// 実働日を単位とした日数計算。
/// <para>
/// <b>境界規則は「<c>from</c> は数えず <c>to</c> は数える」で統一する。</b>
/// 半開区間 <c>(from, to]</c> を数えると考えればよい。この規則により
/// <c>CountBetween(base, AddWorkingDays(base, n)) == n</c> が常に成り立つ。
/// </para>
/// <para>
/// データの登録範囲外が絡む計算は、暦日で誤魔化さず null を返す。呼び出し側が明示的に
/// 暦日へフォールバックする（要件書 4.4）。
/// </para>
/// </summary>
public sealed class WorkingDayMath(WorkingDayCalendar calendar)
{
    private readonly WorkingDayCalendar _calendar =
        calendar ?? throw new ArgumentNullException(nameof(calendar));

    /// <summary>計算に使っているカレンダー。</summary>
    public WorkingDayCalendar Calendar => _calendar;

    /// <summary>
    /// 期間の実働日数。<paramref name="from"/> は数えず <paramref name="to"/> は数える。
    /// <para>
    /// <paramref name="from"/> が <paramref name="to"/> より後なら負の値を返す（対称にしてある）。
    /// どちらかの端がデータの登録範囲外なら null。
    /// </para>
    /// </summary>
    public int? CountBetween(DateOnly from, DateOnly to)
    {
        if (!_calendar.HasDataFor(from) || !_calendar.HasDataFor(to)) return null;

        // (from, to] の件数。from > to のときは自然に負になる。
        return _calendar.UpperBound(to) - _calendar.UpperBound(from);
    }

    /// <summary>
    /// 基準日から <paramref name="n"/> 実働日進んだ（<paramref name="n"/> が負なら戻った）日付。
    /// <para>
    /// <paramref name="n"/> が正なら「基準日より後の <paramref name="n"/> 番目の実働日」、
    /// 負なら「基準日より前の <c>|n|</c> 番目の実働日」、0 なら基準日そのもの。
    /// 基準日自体は稼働日でなくてもよい。
    /// </para>
    /// <para>
    /// 基準日がデータ範囲外、または進み切る前にデータが尽きる場合は null（到達不能）。
    /// </para>
    /// </summary>
    public DateOnly? AddWorkingDays(DateOnly baseDate, int n)
    {
        if (!_calendar.HasDataFor(baseDate)) return null;
        if (n == 0) return baseDate;

        var days = _calendar.DaysArray;

        // n > 0: baseDate より大きい最初の実働日から n 個目
        // n < 0: baseDate より小さい最後の実働日から |n| 個目
        var index = n > 0
            ? _calendar.UpperBound(baseDate) + n - 1
            : _calendar.LowerBound(baseDate) + n;

        if (index < 0 || index >= days.Length) return null;

        var result = days[index];
        return _calendar.HasDataFor(result) ? result : null;
    }

    /// <summary>基準日より<b>前</b>の直近の実働日。無ければ null。</summary>
    public DateOnly? PreviousWorkingDay(DateOnly date) => AddWorkingDays(date, -1);

    /// <summary>基準日より<b>後</b>の直近の実働日。無ければ null。</summary>
    public DateOnly? NextWorkingDay(DateOnly date) => AddWorkingDays(date, 1);

    /// <summary>
    /// 基準日が実働日ならその日、そうでなければ直前の実働日。
    /// 期限日が非稼働日だったときに直前の実働日へ寄せる用途（要件書 4.4）。
    /// </summary>
    public DateOnly? PreviousWorkingDayOrSame(DateOnly date)
    {
        if (!_calendar.HasDataFor(date)) return null;
        return _calendar.IsWorkingDay(date) ? date : PreviousWorkingDay(date);
    }

    /// <summary>基準日が実働日ならその日、そうでなければ直後の実働日。</summary>
    public DateOnly? NextWorkingDayOrSame(DateOnly date)
    {
        if (!_calendar.HasDataFor(date)) return null;
        return _calendar.IsWorkingDay(date) ? date : NextWorkingDay(date);
    }
}
