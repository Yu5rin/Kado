using System.Globalization;

namespace SlideinaCalendar.Core.WorkingDays;

/// <summary>
/// タスクの期限をどう表示するかを一手に担う。
/// <para>規則（要件書 4.4）:</para>
/// <list type="table">
///   <item><term>実働日データがある範囲</term><description>「残り 3実働日」</description></item>
///   <item><term>データが無い範囲</term><description>「残り 189日」（単位を落として暦日表記）</description></item>
///   <item><term>期限が今日</term><description>「今日まで」</description></item>
///   <item><term>期限を過ぎている</term><description>「3実働日 遅れ」</description></item>
///   <item><term>期限日そのものが非稼働日</term><description>「残り 2実働日（9/25まで）」</description></item>
/// </list>
/// <para>
/// 境界規則は<b>今日は数えず、期限日は数える</b>。データ範囲の内外が混在する場合は暦日側に倒す。
/// </para>
/// </summary>
public sealed class DueDateFormatter(WorkingDayMath math)
{
    private readonly WorkingDayMath _math =
        math ?? throw new ArgumentNullException(nameof(math));

    /// <summary>カレンダーから直接組み立てる場合の簡易コンストラクタ。</summary>
    public DueDateFormatter(WorkingDayCalendar calendar)
        : this(new WorkingDayMath(calendar)) { }

    private WorkingDayCalendar Calendar => _math.Calendar;

    /// <summary>
    /// 期限の残日数表記を組み立てる。
    /// </summary>
    /// <param name="due">タスクの期限日。</param>
    /// <param name="today">今日の日付。</param>
    public DueText Format(DateOnly due, DateOnly today)
    {
        // 1. 期限が今日。実働日データの有無に関わらず最優先。
        if (due == today)
        {
            return new DueText("今日まで", DueKind.Today, due, IsSnapped: false, 0, IsCalendarUnit: false);
        }

        // 2. 両端が実働日データの範囲に収まっているときだけ実働日で数える。
        //    片側でも範囲外なら数え間違えるので、混在は暦日側に倒す。
        if (Calendar.HasDataFor(due) && Calendar.HasDataFor(today)
            && _math.PreviousWorkingDayOrSame(due) is { } effective)
        {
            var snapped = effective != due;

            // 2-a. 寄せた結果が今日になった（期限は明日以降の非稼働日だが、実働日では今日が最後）
            if (effective == today)
            {
                return new DueText("今日まで", DueKind.Today, effective, snapped, 0, IsCalendarUnit: false);
            }

            // 2-b. まだ先。今日は数えず期限日は数えるので必ず 1 以上になる。
            if (effective > today)
            {
                var remaining = _math.CountBetween(today, effective)!.Value;
                return new DueText(
                    $"残り {remaining}実働日{SnapSuffix(snapped, effective)}",
                    DueKind.WorkingDays, effective, snapped, remaining, IsCalendarUnit: false);
            }

            // 2-c. 過ぎている。
            var overdue = _math.CountBetween(effective, today)!.Value;
            if (overdue >= 1)
            {
                return new DueText(
                    $"{overdue}実働日 遅れ{SnapSuffix(snapped, effective)}",
                    DueKind.Overdue, effective, snapped, overdue, IsCalendarUnit: false);
            }

            // 今日が非稼働日で、寄せた期限との間に実働日が1日も無いケース。
            // 「0実働日 遅れ」では意味をなさないので、ここだけ暦日に倒す。
            var overdueCalendar = today.DayNumber - effective.DayNumber;
            return new DueText(
                $"{overdueCalendar}日 遅れ{SnapSuffix(snapped, effective)}",
                DueKind.Overdue, effective, snapped, overdueCalendar, IsCalendarUnit: true);
        }

        // 3. 暦日フォールバック。データが無い／範囲をまたぐ場合。
        var diff = due.DayNumber - today.DayNumber;
        return diff > 0
            ? new DueText($"残り {diff}日", DueKind.CalendarDays, due, false, diff, IsCalendarUnit: true)
            : new DueText($"{-diff}日 遅れ", DueKind.Overdue, due, false, -diff, IsCalendarUnit: true);
    }

    /// <summary>
    /// 非稼働日を直前の実働日へ寄せたとき、寄せた先を明示する括弧書き。
    /// 「どの日を基準に数えたのか」が分からないと数字を信用できないため必ず出す。
    /// </summary>
    private static string SnapSuffix(bool snapped, DateOnly effective) =>
        snapped ? $"（{effective.ToString("M/d", CultureInfo.InvariantCulture)}まで）" : string.Empty;
}
