using Kado.Core.WorkingDays;

namespace Kado.Presentation.ViewModels;

/// <summary>
/// 日付に添える一言。祝日名と、実働日データから分かる「休業日」「特別出勤」。
/// <para>
/// 休業日・特別出勤は、もとはマスの色（沈める・起こす）だけで示していた。ところが
/// 色だけでは、取り込んだあとに何も変わっていないように見え、「取り込んでも何も
/// 表示されない」と受け取られた。祝日名と同じ場所に文字でも出す。
/// </para>
/// </summary>
/// <param name="Text">出す文字。無ければ null。</param>
/// <param name="IsOpenDay">休みのはずの日に稼働する（特別出勤）か。色を分けるのに使う。</param>
public sealed record DayNote(string? Text, bool IsOpenDay)
{
    /// <summary>平日なのに休む日の文言。</summary>
    public const string ClosedDayText = CalendarWorkspace.ClosedDayTitle;

    /// <summary>土日祝なのに稼働する日の文言。</summary>
    public const string OpenDayText = CalendarWorkspace.OpenDayTitle;

    /// <summary>
    /// その日の一言を決める。
    /// <list type="bullet">
    ///   <item>祝日で休み → 祝日名（例「海の日」）</item>
    ///   <item>祝日だが稼働 → 「海の日・特別出勤」</item>
    ///   <item>土日だが稼働 → 「特別出勤」</item>
    ///   <item>平日だが休み → 「休業日」</item>
    /// </list>
    /// 実働日データの範囲外は、休み・稼働を判断できないので祝日名だけにする。
    /// </summary>
    public static DayNote Of(DateOnly date, string? holidayName, WorkingDayCalendar workingDays)
    {
        ArgumentNullException.ThrowIfNull(workingDays);

        var holiday = holidayName is { Length: > 0 } ? holidayName : null;
        if (!workingDays.HasDataFor(date)) return new DayNote(holiday, false);

        var working = workingDays.IsWorkingDay(date);
        var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        if (working && (holiday is not null || weekend))
        {
            return new DayNote(holiday is null ? OpenDayText : $"{holiday}・{OpenDayText}", true);
        }

        if (!working && holiday is null && !weekend) return new DayNote(ClosedDayText, false);

        return new DayNote(holiday, false);
    }
}
