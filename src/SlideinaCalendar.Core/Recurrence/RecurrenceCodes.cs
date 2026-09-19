namespace SlideinaCalendar.Core.Recurrence;

/// <summary>
/// 繰り返し指定で使う記号と日本語表示の対応表。
/// </summary>
internal static class RecurrenceCodes
{
    private static readonly (string Code, DayOfWeek Day)[] Days =
    [
        ("SU", DayOfWeek.Sunday),
        ("MO", DayOfWeek.Monday),
        ("TU", DayOfWeek.Tuesday),
        ("WE", DayOfWeek.Wednesday),
        ("TH", DayOfWeek.Thursday),
        ("FR", DayOfWeek.Friday),
        ("SA", DayOfWeek.Saturday),
    ];

    /// <summary>日本語の曜日名（日〜土）。<see cref="DayOfWeek"/> の値をそのまま添字に使う。</summary>
    private static readonly string[] JapaneseDayNames = ["日", "月", "火", "水", "木", "金", "土"];

    public static DayOfWeek ParseDay(string code)
    {
        foreach (var (c, d) in Days)
        {
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return d;
        }
        throw new FormatException($"曜日の指定が不正です: '{code}'（SU/MO/TU/WE/TH/FR/SA のいずれか）");
    }

    public static string ToCode(DayOfWeek day) => Days[(int)day].Code;

    /// <summary>「火曜」のような表示用の曜日名。</summary>
    public static string ToLabel(DayOfWeek day) => JapaneseDayNames[(int)day] + "曜";

    /// <summary>月曜を週の先頭とした、その日が属する週の先頭の通し日数（RFC 5545 の既定 WKST=MO）。</summary>
    public static int WeekStartDayNumber(DateOnly date) =>
        date.DayNumber - ((int)date.DayOfWeek + 6) % 7;
}
