using System.Globalization;
using System.Text.RegularExpressions;

namespace SlideinaCalendar.Presentation.Editing;

/// <summary>
/// 時刻の打ち込みを読む。
/// <para>
/// 「9」「930」「9:30」「09:30」のどれでも受ける。時刻を入れるたびにコロンと
/// 0 埋めを要求されるのは、1日に何度も触る画面では煩わしい。
/// </para>
/// <para>
/// 候補から選んだときの「10:30（1時間30分）」のような文字列も、頭の時刻だけ読む。
/// 表示のための添え字を消してから渡す必要をなくすため。
/// </para>
/// </summary>
public static class TimeInput
{
    /// <summary>「9:30」「9：30」。全角コロンも受ける。</summary>
    private static readonly Regex Separated = new(@"^\s*(\d{1,2})\s*[:：]\s*(\d{1,2})", RegexOptions.Compiled);

    /// <summary>「9」「930」「0930」。区切りなし。</summary>
    private static readonly Regex Packed = new(@"^\s*(\d{1,4})\s*$", RegexOptions.Compiled);

    /// <summary>読めれば時刻、読めなければ null。</summary>
    public static TimeOnly? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var normalized = Normalize(text);

        if (Separated.Match(normalized) is { Success: true } separated)
        {
            return Build(Number(separated, 1), Number(separated, 2));
        }

        if (Packed.Match(normalized) is { Success: true } packed)
        {
            var digits = packed.Groups[1].Value;

            return digits.Length switch
            {
                // 「9」「12」は時だけ
                1 or 2 => Build(int.Parse(digits, CultureInfo.InvariantCulture), 0),
                // 「930」→ 9:30、「1330」→ 13:30
                3 => Build(int.Parse(digits[..1], CultureInfo.InvariantCulture),
                           int.Parse(digits[1..], CultureInfo.InvariantCulture)),
                4 => Build(int.Parse(digits[..2], CultureInfo.InvariantCulture),
                           int.Parse(digits[2..], CultureInfo.InvariantCulture)),
                _ => null,
            };
        }

        return null;
    }

    /// <summary>「09:30」。画面に出すときの形。</summary>
    public static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// 「1時間30分」。長さの表示。
    /// <para>0 以下なら null。日をまたぐ予定は終了日のほうで表すので、ここでは扱わない。</para>
    /// </summary>
    public static string? FormatDuration(TimeOnly from, TimeOnly to)
    {
        // TimeOnly の引き算は日をまたいで回り込む。9:00 - 10:00 が 23 時間になるので、
        // 前後を先に確かめる
        if (to <= from) return null;

        var minutes = (int)(to - from).TotalMinutes;
        if (minutes <= 0) return null;

        var (hours, rest) = (minutes / 60, minutes % 60);

        return (hours, rest) switch
        {
            (0, _) => $"{rest}分",
            (_, 0) => $"{hours}時間",
            _ => $"{hours}時間{rest}分",
        };
    }

    /// <summary>15分刻みの候補。打つより選ぶほうが速いときのため。</summary>
    public static IReadOnlyList<string> EveryQuarterHour()
    {
        var result = new List<string>(24 * 4);

        for (var time = new TimeOnly(0, 0); ; time = time.AddMinutes(15))
        {
            result.Add(Format(time));
            if (time.Hour == 23 && time.Minute == 45) break;
        }

        return result;
    }

    /// <summary>全角の数字とコロンを半角に寄せる。日本語入力のまま打てるように。</summary>
    private static string Normalize(string text)
    {
        Span<char> buffer = stackalloc char[text.Length];

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            buffer[i] = c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c;
        }

        return new string(buffer);
    }

    private static int Number(Match match, int group) =>
        int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static TimeOnly? Build(int hour, int minute) =>
        hour is >= 0 and <= 23 && minute is >= 0 and <= 59 ? new TimeOnly(hour, minute) : null;
}
