using System.Globalization;

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

    /// <summary>
    /// 序数つきの曜日（<c>2TU</c> ＝第2火曜、<c>-1FR</c> ＝最終金曜）を読む。
    /// <para>
    /// 序数が無ければ（<c>TU</c> だけなら）戻り値の <c>Ordinal</c> は 0
    /// （「毎回」の意味。呼び出し側のパターン実装で扱いを決める）。
    /// </para>
    /// </summary>
    public static (int Ordinal, DayOfWeek Day) ParseOrdinalDay(string code)
    {
        var trimmed = code.Trim();
        if (trimmed.Length < 2)
        {
            throw new FormatException($"曜日の指定が不正です: '{code}'（SU/MO/TU/WE/TH/FR/SA のいずれか）");
        }

        var dayCode = trimmed[^2..];
        var ordinalPart = trimmed[..^2];
        var day = ParseDay(dayCode);

        if (ordinalPart.Length == 0) return (0, day);

        if (!int.TryParse(ordinalPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal)
            || ordinal == 0)
        {
            throw new FormatException($"曜日の順序指定が不正です: '{code}'（例: 2TU、-1FR）");
        }

        return (ordinal, day);
    }

    public static string ToCode(DayOfWeek day) => Days[(int)day].Code;

    /// <summary>序数つきの指定文字列に戻す（<c>2TU</c> / <c>-1FR</c>）。序数が 0 なら曜日だけ。</summary>
    public static string ToOrdinalCode(int ordinal, DayOfWeek day) =>
        ordinal == 0 ? ToCode(day) : ordinal.ToString(CultureInfo.InvariantCulture) + ToCode(day);

    /// <summary>「火曜」のような表示用の曜日名。</summary>
    public static string ToLabel(DayOfWeek day) => JapaneseDayNames[(int)day] + "曜";

    /// <summary>「第2火曜」「最終金曜」のような表示用文字列。序数が 0 なら曜日だけ。</summary>
    public static string ToOrdinalLabel(int ordinal, DayOfWeek day)
    {
        var name = ToLabel(day);
        return ordinal switch
        {
            0 => name,
            -1 => $"最終{name}",
            < 0 => $"最終から{-ordinal - 1}回前の{name}",
            _ => $"第{ordinal.ToString(CultureInfo.InvariantCulture)}{name}",
        };
    }

    /// <summary>月曜を週の先頭とした、その日が属する週の先頭の通し日数（RFC 5545 の既定 WKST=MO）。</summary>
    public static int WeekStartDayNumber(DateOnly date) =>
        date.DayNumber - ((int)date.DayOfWeek + 6) % 7;
}
