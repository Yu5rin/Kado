using System.Collections.Immutable;
using SlideinaCalendar.Core.Import;

namespace SlideinaCalendar.Core.WorkingDays;

/// <summary>
/// 会社の実働日（稼働日）の集合を保持し、判定と集計を行う不変オブジェクト。
/// <para>
/// 稼働日は「登録された日付の集合」であって「土日祝を除いた日」ではない。したがって
/// データの登録範囲外は土日祝から推測せず、<see cref="HasDataFor"/> が false を返す
/// 「データなし」として扱う。呼び出し側はそれを見て暦日にフォールバックする。
/// </para>
/// <para>
/// 数万件を想定するため、稼働判定は <see cref="HashSet{T}"/> による O(1)、
/// 範囲集計は昇順配列への二分探索による O(log n) で行う。
/// </para>
/// </summary>
public sealed class WorkingDayCalendar
{
    private readonly ImmutableArray<DateOnly> _days;          // 昇順・重複なし
    private readonly FrozenSetLike _lookup;                   // O(1) 判定用
    private readonly ImmutableDictionary<DateOnly, ImmutableArray<Milestone>> _milestones;

    /// <summary>稼働日データが1件も入っていない空のカレンダー。</summary>
    public static WorkingDayCalendar Empty { get; } = new(
        ImmutableArray<DateOnly>.Empty,
        null,
        null,
        ImmutableDictionary<DateOnly, ImmutableArray<Milestone>>.Empty,
        null,
        null);

    private WorkingDayCalendar(
        ImmutableArray<DateOnly> days,
        DateOnly? rangeStart,
        DateOnly? rangeEnd,
        ImmutableDictionary<DateOnly, ImmutableArray<Milestone>> milestones,
        DateOnly? milestoneRangeStart,
        DateOnly? milestoneRangeEnd)
    {
        _days = days;
        _lookup = new FrozenSetLike(days);
        _milestones = milestones;
        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
        MilestoneRangeStart = milestoneRangeStart;
        MilestoneRangeEnd = milestoneRangeEnd;
    }

    /// <summary>稼働日データの登録範囲の開始日。データが無ければ null。</summary>
    public DateOnly? RangeStart { get; }

    /// <summary>稼働日データの登録範囲の終了日。データが無ければ null。</summary>
    public DateOnly? RangeEnd { get; }

    /// <summary>
    /// マイルストーンの登録範囲の開始日。稼働日とは期間が異なるため別に持つ
    /// （実ファイルでは稼働日 2023/1/5〜2026/3/31 に対しマイルストーンは 2025/1/10〜2026/3/26）。
    /// </summary>
    public DateOnly? MilestoneRangeStart { get; }

    /// <summary>マイルストーンの登録範囲の終了日。</summary>
    public DateOnly? MilestoneRangeEnd { get; }

    /// <summary>登録されている稼働日の件数。</summary>
    public int Count => _days.Length;

    /// <summary>登録されている稼働日（昇順）。</summary>
    public IReadOnlyList<DateOnly> Days => _days;

    /// <summary>登録されているマイルストーン（日付昇順、同日内は登録順）。</summary>
    public IReadOnlyList<Milestone> AllMilestones =>
        _milestones.Keys.Order().SelectMany(d => _milestones[d]).ToArray();

    // ----------------------------------------------------------------------
    // 生成
    // ----------------------------------------------------------------------

    /// <summary>
    /// 稼働日の集合からカレンダーを作る。登録範囲は与えた日付の最小・最大から決まる。
    /// </summary>
    public static WorkingDayCalendar Create(
        IEnumerable<DateOnly> workingDays,
        IEnumerable<Milestone>? milestones = null)
        => Create(workingDays, null, null, milestones, null, null);

    /// <summary>
    /// 稼働日の集合と登録範囲を明示してカレンダーを作る。
    /// <para>
    /// 範囲を明示できるようにしているのは、月の途中から始まるファイルを取り込んだときに
    /// 「範囲内だが稼働日が無い日」と「そもそも範囲外の日」を区別するため。
    /// null を渡した場合は与えた日付の最小・最大を範囲とする。
    /// </para>
    /// </summary>
    public static WorkingDayCalendar Create(
        IEnumerable<DateOnly> workingDays,
        DateOnly? rangeStart,
        DateOnly? rangeEnd,
        IEnumerable<Milestone>? milestones,
        DateOnly? milestoneRangeStart,
        DateOnly? milestoneRangeEnd)
    {
        ArgumentNullException.ThrowIfNull(workingDays);

        var days = workingDays.Distinct().Order().ToImmutableArray();

        var msList = (milestones ?? []).ToArray();
        var msMap = msList
            .GroupBy(m => m.Date)
            .ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray());

        var start = rangeStart ?? (days.Length > 0 ? days[0] : null);
        var end = rangeEnd ?? (days.Length > 0 ? days[^1] : null);

        var msStart = milestoneRangeStart ?? (msList.Length > 0 ? msList.Min(m => m.Date) : null);
        var msEnd = milestoneRangeEnd ?? (msList.Length > 0 ? msList.Max(m => m.Date) : null);

        return new WorkingDayCalendar(days, start, end, msMap, msStart, msEnd);
    }

    /// <summary>
    /// 取り込み結果をこのカレンダーに反映した新しいカレンダーを返す。
    /// <para>
    /// <b>既存データは全置換しない。</b>ファイルに含まれる期間だけを置き換え、期間外の既存データは残す。
    /// 稼働日とマイルストーンは期間が異なるため、それぞれの期間で判定する。
    /// 古いファイルを誤って読み込んでも過去データが消えないようにするための規則（要件書 4.1）。
    /// </para>
    /// </summary>
    public WorkingDayCalendar Merge(ImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // --- 稼働日: ファイルの期間内だけ差し替える ---
        var ws = result.WorkingDayRangeStart;
        var we = result.WorkingDayRangeEnd;
        var mergedDays = _days
            .Where(d => d < ws || d > we)     // 期間外の既存データは残す
            .Concat(result.WorkingDays)
            .Distinct()
            .Order()
            .ToImmutableArray();

        // --- マイルストーン: 稼働日とは別の期間で差し替える ---
        var keptMilestones = _milestones.Values.SelectMany(v => v).AsEnumerable();
        if (result.MilestoneRangeStart is { } ms && result.MilestoneRangeEnd is { } me)
        {
            keptMilestones = keptMilestones.Where(m => m.Date < ms || m.Date > me);
        }
        var mergedMilestones = keptMilestones.Concat(result.Milestones).ToArray();

        // --- 登録範囲も広げる（狭めない） ---
        var newStart = Min(RangeStart, ws);
        var newEnd = Max(RangeEnd, we);
        var newMsStart = Min(MilestoneRangeStart, result.MilestoneRangeStart);
        var newMsEnd = Max(MilestoneRangeEnd, result.MilestoneRangeEnd);

        return Create(mergedDays, newStart, newEnd, mergedMilestones, newMsStart, newMsEnd);

        static DateOnly? Min(DateOnly? a, DateOnly? b) =>
            a is null ? b : b is null ? a : a.Value < b.Value ? a : b;
        static DateOnly? Max(DateOnly? a, DateOnly? b) =>
            a is null ? b : b is null ? a : a.Value > b.Value ? a : b;
    }

    // ----------------------------------------------------------------------
    // 判定
    // ----------------------------------------------------------------------

    /// <summary>指定日が稼働日として登録されているか。O(1)。</summary>
    public bool IsWorkingDay(DateOnly date) => _lookup.Contains(date);

    /// <summary>
    /// 指定日が実働日データの登録範囲内か。
    /// <para>
    /// false のときは「非稼働日」ではなく「判定できない」。呼び出し側は暦日にフォールバックすること。
    /// </para>
    /// </summary>
    public bool HasDataFor(DateOnly date) =>
        RangeStart is { } s && RangeEnd is { } e && date >= s && date <= e;

    /// <summary>指定日がマイルストーンの登録範囲内か。</summary>
    public bool HasMilestoneDataFor(DateOnly date) =>
        MilestoneRangeStart is { } s && MilestoneRangeEnd is { } e && date >= s && date <= e;

    /// <summary>
    /// その月の何実働日目か（1 始まり）。非稼働日およびデータ範囲外は null。
    /// <para>
    /// 月の途中からデータが始まっている場合は、登録されている範囲だけで数えた値になる。
    /// 完全な月かどうかは <see cref="IsMonthFullyCovered"/> で確認できる。
    /// </para>
    /// </summary>
    public int? IndexInMonth(DateOnly date)
    {
        if (!HasDataFor(date) || !IsWorkingDay(date)) return null;

        var monthStart = new DateOnly(date.Year, date.Month, 1);
        var firstIndex = LowerBound(monthStart);
        var dateIndex = LowerBound(date);          // date は稼働日なので _days[dateIndex] == date
        return dateIndex - firstIndex + 1;
    }

    /// <summary>
    /// その月に登録されている実働日の数。
    /// <para>
    /// 月の一部しかデータが無い場合は部分的な件数を返す。UI で「今月の実働日 ◯日」として
    /// 出す前に <see cref="IsMonthFullyCovered"/> を確認すること。
    /// </para>
    /// </summary>
    public int CountInMonth(int year, int month)
    {
        var monthStart = new DateOnly(year, month, 1);
        var nextMonth = monthStart.AddMonths(1);
        return LowerBound(nextMonth) - LowerBound(monthStart);
    }

    /// <summary>その月が丸ごと実働日データの登録範囲に収まっているか。</summary>
    public bool IsMonthFullyCovered(int year, int month)
    {
        var monthStart = new DateOnly(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        return HasDataFor(monthStart) && HasDataFor(monthEnd);
    }

    /// <summary>
    /// その月の残り実働日数。<paramref name="today"/> は数えず、以降の実働日を数える。
    /// ツールバーの「今月の実働日 ◯日 ／ 残り ◯日」の後半に使う。
    /// </summary>
    public int RemainingInMonth(DateOnly today)
    {
        var nextMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(1);
        return LowerBound(nextMonth) - UpperBound(today);
    }

    /// <summary>指定日に登録されているマイルストーン。無ければ空。</summary>
    public IReadOnlyList<Milestone> MilestonesOn(DateOnly date) =>
        _milestones.TryGetValue(date, out var list) ? list : ImmutableArray<Milestone>.Empty;

    // ----------------------------------------------------------------------
    // 二分探索（WorkingDayMath から使う）
    // ----------------------------------------------------------------------

    internal ImmutableArray<DateOnly> DaysArray => _days;

    /// <summary><paramref name="date"/> 以上の最初の要素の位置。無ければ <see cref="Count"/>。</summary>
    internal int LowerBound(DateOnly date)
    {
        int lo = 0, hi = _days.Length;
        while (lo < hi)
        {
            var mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (_days[mid] < date) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary><paramref name="date"/> より大きい最初の要素の位置。無ければ <see cref="Count"/>。</summary>
    internal int UpperBound(DateOnly date)
    {
        int lo = 0, hi = _days.Length;
        while (lo < hi)
        {
            var mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (_days[mid] <= date) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// O(1) 判定用の集合。<see cref="HashSet{T}"/> を直接持つと可変に見えるため薄く包む。
    /// </summary>
    private sealed class FrozenSetLike(ImmutableArray<DateOnly> days)
    {
        private readonly HashSet<DateOnly> _set = [.. days];
        public bool Contains(DateOnly date) => _set.Contains(date);
    }
}
