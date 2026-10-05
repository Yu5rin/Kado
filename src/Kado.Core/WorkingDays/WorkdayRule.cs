namespace Kado.Core.WorkingDays;

/// <summary>
/// 「この日は稼働日か」を決める。登録された稼働日データを優先し、データの無い日は
/// 土日と祝日を除いた日を稼働日とみなす。
/// <para>
/// <see cref="WorkingDayCalendar"/> は「登録された日の集合」で、登録範囲の外では
/// 「判定できない」（<see cref="WorkingDayCalendar.HasDataFor"/> が false）。タスクの繰り返しは
/// 何か月も先まで日を探すので、データの尽きたところで止まらず、暦の既定（平日は稼働、
/// 土日祝は休み）で続ける。<c>WorkingDayMarks.Rebuild</c> が印から稼働日を組み立てるときの
/// 既定の規則と同じ考え方。
/// </para>
/// <para>
/// 祝日は <see cref="JapaneseHolidays"/> が既定。取り込んだ祝日の一覧を使いたい呼び出し側は
/// <c>isHoliday</c> を渡す。
/// </para>
/// </summary>
public sealed class WorkdayRule
{
    private readonly WorkingDayCalendar _calendar;
    private readonly Func<DateOnly, bool> _isHoliday;

    /// <summary>稼働日データが無いときの規則（土日祝を除いた日）。</summary>
    public static WorkdayRule Default { get; } = new(WorkingDayCalendar.Empty);

    /// <param name="calendar">会社の稼働日。登録範囲の中ではこれが決める。</param>
    /// <param name="isHoliday">祝日か。渡さなければ <see cref="JapaneseHolidays"/>。</param>
    public WorkdayRule(WorkingDayCalendar calendar, Func<DateOnly, bool>? isHoliday = null)
    {
        _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
        _isHoliday = isHoliday ?? (static date => JapaneseHolidays.NameOf(date) is { Length: > 0 });
    }

    /// <summary>その日は稼働日か。</summary>
    public bool IsWorkday(DateOnly date) =>
        _calendar.HasDataFor(date)
            ? _calendar.IsWorkingDay(date)
            : date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !_isHoliday(date);
}
