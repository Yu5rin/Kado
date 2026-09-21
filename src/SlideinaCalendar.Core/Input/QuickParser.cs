using System.Globalization;
using System.Text.RegularExpressions;

namespace SlideinaCalendar.Core.Input;

/// <summary>
/// 「明日15時 打合せ @会議室A」のような1行から、予定を読み取る。
/// <para>
/// 旧 inaCalendar の解釈規則をそのまま移した。読めた部分は文字列から取り除き、
/// 残ったものを題にする。
/// </para>
/// <para>
/// <b>読めない言い回しは、登録を止める材料として返す。</b>「毎週」「終日」のような
/// 言葉を無視して一部だけ解釈すると、書いたつもりと違うものが黙って入る。
/// </para>
/// </summary>
public static class QuickParser
{
    /// <summary>1行を読み取る。</summary>
    /// <param name="text">打ち込まれた1行。</param>
    /// <param name="baseDate">
    /// 「明日」「来週の火曜」「3日後」のような<b>相対語</b>の基準日。ふつうは今日。
    /// 相対語は選択中の日ではなく、常にこの日から数える（選んでいる日の翌日が
    /// 「明日」になるのは直感に反するため）。
    /// </param>
    /// <param name="explicitBase">
    /// 日付をまったく書いていないときや、「15日」のように月を省いた書き方をしたときの
    /// 基準日。省略すると <paramref name="baseDate"/> と同じになる。呼び出し側が
    /// 選択中の日を渡せば、「打合せ 15時」のように日付なしで打った1行はその日に入る。
    /// </param>
    public static QuickEntry Parse(string? text, DateOnly baseDate, DateOnly? explicitBase = null)
    {
        // タスクの印は行の先頭でしか見ない。文中の「-」まで拾うと誤検知が増える
        var (body, kind) = ReadKind((text ?? string.Empty).TrimStart());

        var rest = " " + body.Trim() + " ";

        // 「終日」は時刻を書かなければどのみち終日になるので、読み飛ばすだけでよい
        // （読めない言い回しとして止める必要が無い）。他の解釈が食い荒らす前に外す
        rest = Replace(rest, "終日");

        // 読めない言い回しは、下の解釈が食い荒らす前に見つけておく
        var unsupported = Unsupported(rest);

        var (date, dateError) = ReadDate(ref rest, baseDate, explicitBase ?? baseDate);
        var (start, end, timeError) = ReadTime(ref rest);
        var location = ReadLocation(ref rest);

        var title = Whitespace.Replace(rest, " ").Trim();

        return new QuickEntry(title, date, start, end, location, dateError, timeError, unsupported, kind);
    }

    // ------------------------------------------------------------------
    // タスクかどうかの判定
    // ------------------------------------------------------------------

    /// <summary>
    /// 先頭の印からタスクかどうかを判定し、印を取り除いた残りを返す。
    /// <para>
    /// 「□ 部品表確認」「- 部品表確認」「todo 部品表確認」「タスク: 部品表確認」のような
    /// 書き出しをタスクと読む。「-」は日付の区切り（「9/1-9/5」など）と衝突しないよう、
    /// <b>直後に空白があるときだけ</b>印として扱う。
    /// </para>
    /// </summary>
    private static (string Body, QuickEntryKind Kind) ReadKind(string text)
    {
        var match = TaskMarkerPattern.Match(text);
        return match.Success
            ? (text[match.Length..], QuickEntryKind.Task)
            : (text, QuickEntryKind.Event);
    }

    private static readonly Regex TaskMarkerPattern = new(
        @"^(?:[□☐]\s*|[-－]\s+|todo\s*[:：]?\s*|タスク\s*[:：]?\s*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ------------------------------------------------------------------
    // 日付
    // ------------------------------------------------------------------

    private static (DateOnly Date, bool Error) ReadDate(ref string rest, DateOnly baseDate, DateOnly explicitBase)
    {
        // 1. 「明日」「あさって」など（相対語なので今日基準のまま）
        foreach (var (word, offset) in RelativeDays)
        {
            if (!rest.Contains(word, StringComparison.Ordinal)) continue;

            rest = Replace(rest, word);
            return (baseDate.AddDays(offset), false);
        }

        // 2. 「来週の火曜」「木曜」（相対語なので今日基準のまま）
        if (ReadWeekday(ref rest, baseDate) is { } weekday) return (weekday, false);

        // 3. 「来月15日」「来月」（相対語なので今日基準のまま）
        if (ReadNextMonth(ref rest, baseDate) is { } nextMonth) return nextMonth;

        // 4. 年つき、月日、M/D、N日後、N日（日付を書いていない・月を省いたときは
        //    explicitBase＝選んでいる日を基準にする）
        return ReadExplicitDate(ref rest, baseDate, explicitBase);
    }

    private static DateOnly? ReadWeekday(ref string rest, DateOnly baseDate)
    {
        // 「再来週」は文字として「来週」を含む。先に見ないと「再」だけが残る
        int? weekOffset = null;
        foreach (var (word, offset) in WeekWords)
        {
            if (!rest.Contains(word, StringComparison.Ordinal)) continue;

            weekOffset = offset;
            rest = Replace(rest, word);
            break;
        }

        var match = WeekdayPattern.Match(rest);
        if (match.Success)
        {
            var target = WeekdayNames.IndexOf(match.Groups[1].Value, StringComparison.Ordinal);
            rest = Replace(rest, match.Value);

            // 週を指していればその週の当該曜日、指していなければ次に来るその曜日
            return weekOffset is { } offset
                ? baseDate.AddDays(-(int)baseDate.DayOfWeek + target + offset)
                : baseDate.AddDays(((target - (int)baseDate.DayOfWeek) + 7) % 7);
        }

        return weekOffset is { } only ? baseDate.AddDays(only) : null;
    }

    private static (DateOnly Date, bool Error)? ReadNextMonth(ref string rest, DateOnly baseDate)
    {
        var withDay = NextMonthDayPattern.Match(rest);
        if (withDay.Success)
        {
            rest = Replace(rest, withDay.Value);
            var head = baseDate.AddMonths(1);
            return TryDigits(withDay.Groups[1].Value, out var day)
                ? Build(head.Year, head.Month, day, baseDate)
                : (baseDate, true);
        }

        if (!rest.Contains("来月", StringComparison.Ordinal)) return null;

        rest = Replace(rest, "来月");

        // 日を書いていなければ同じ日。月末は繰り上がる（3/31 の来月は 4/30 ではなく 5/1）
        return (baseDate.AddMonths(1), false);
    }

    private static (DateOnly Date, bool Error) ReadExplicitDate(ref string rest, DateOnly baseDate, DateOnly explicitBase)
    {
        // 年つきは曖昧さが無い。先に見ないと「26/12」が M/D に当たる
        var full = FullDatePattern.Match(rest);
        if (full.Success)
        {
            rest = Replace(rest, full.Value);
            return TryDigits(full.Groups[1].Value, out var year)
                && TryDigits(full.Groups[2].Value, out var month)
                && TryDigits(full.Groups[3].Value, out var day)
                ? Build(year, month, day, baseDate)
                : (baseDate, true);
        }

        foreach (var pattern in MonthDayPatterns)
        {
            var match = pattern.Match(rest);
            if (!match.Success) continue;

            rest = Replace(rest, match.Value);

            if (!TryDigits(match.Groups[1].Value, out var month) || !TryDigits(match.Groups[2].Value, out var day))
                return (baseDate, true);

            return Build(NearFutureYear(baseDate, month, day), month, day, baseDate);
        }

        // 「N日後」は「N日」より先に見る
        var after = DaysAfterPattern.Match(rest);
        if (after.Success)
        {
            rest = Replace(rest, after.Value);
            return TryDigits(after.Groups[1].Value, out var daysAfter)
                ? (baseDate.AddDays(daysAfter), false)
                : (baseDate, true);
        }

        // 「N日間」は期間なので日付として取らない。月を書いていないので、
        // 選んでいる日（explicitBase）の月を使う
        var dayOnly = DayOfMonthPattern.Match(rest);
        if (dayOnly.Success)
        {
            rest = Replace(rest, dayOnly.Value);
            return TryDigits(dayOnly.Groups[1].Value, out var dayOfMonth)
                ? Build(explicitBase.Year, explicitBase.Month, dayOfMonth, explicitBase)
                : (explicitBase, true);
        }

        // 日付をまったく書いていないときは、選んでいる日に入れる
        return (explicitBase, false);
    }

    /// <summary>暦にある日かどうかを確かめて組み立てる。</summary>
    private static (DateOnly Date, bool Error) Build(int year, int month, int day, DateOnly fallback)
    {
        if (month is < 1 or > 12 || day < 1 || year is < 1 or > 9999) return (fallback, true);
        if (day > DateTime.DaysInMonth(year, month)) return (fallback, true);

        return (new DateOnly(year, month, day), false);
    }

    /// <summary>月日だけ書かれたときの年。過ぎていれば翌年と読む。</summary>
    private static int NearFutureYear(DateOnly baseDate, int month, int day)
    {
        if (month is < 1 or > 12 || day < 1) return baseDate.Year;

        var thisYear = day <= DateTime.DaysInMonth(baseDate.Year, month)
            ? new DateOnly(baseDate.Year, month, day)
            : (DateOnly?)null;

        return thisYear is { } candidate && candidate < baseDate ? baseDate.Year + 1 : baseDate.Year;
    }

    // ------------------------------------------------------------------
    // 時刻
    // ------------------------------------------------------------------

    private static (TimeOnly? Start, TimeOnly? End, bool Error) ReadTime(ref string rest)
    {
        // 「10:00-11:00」
        var range = ColonRangePattern.Match(rest);
        if (range.Success)
        {
            rest = Replace(rest, range.Value);
            var from = At(range.Groups[1].Value, range.Groups[2].Value, range.Groups[3].Value);
            var to = At(range.Groups[4].Value, range.Groups[5].Value, range.Groups[6].Value);
            return (from, to, from is null || to is null);
        }

        // 「10時から11時半」
        var kanjiRange = KanjiRangePattern.Match(rest);
        if (kanjiRange.Success)
        {
            rest = Replace(rest, kanjiRange.Value);
            var from = At(kanjiRange.Groups[1].Value, kanjiRange.Groups[2].Value, Minutes(kanjiRange.Groups[3].Value));
            var to = At(kanjiRange.Groups[4].Value, kanjiRange.Groups[5].Value, Minutes(kanjiRange.Groups[6].Value));
            return (from, to, from is null || to is null);
        }

        // 「15:30」
        var colon = ColonPattern.Match(rest);
        if (colon.Success)
        {
            rest = Replace(rest, colon.Value);
            var at = At(colon.Groups[1].Value, colon.Groups[2].Value, colon.Groups[3].Value);
            return (at, null, at is null);
        }

        // 「15時半」。「N時間」は所要時間なので取らない
        var kanji = KanjiPattern.Match(rest);
        if (kanji.Success)
        {
            rest = Replace(rest, kanji.Value);
            var at = At(kanji.Groups[1].Value, kanji.Groups[2].Value, Minutes(kanji.Groups[3].Value));
            return (at, null, at is null);
        }

        return (null, null, false);
    }

    /// <summary>午前・午後を24時制に直して組み立てる。</summary>
    private static TimeOnly? At(string half, string hourText, string minuteText)
    {
        if (!TryDigits(hourText, out var hour)) return null;
        if (!TryDigits(minuteText, out var minute)) minute = 0;

        // 「午前12時」は0時、「午後12時」は正午
        hour = half switch
        {
            "午後" => hour < 12 ? hour + 12 : hour,
            "午前" => hour == 12 ? 0 : hour,
            _ => hour,
        };

        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59 ? new TimeOnly(hour, minute) : null;
    }

    /// <summary>「半」は30分。「15分」は15分。</summary>
    private static string Minutes(string text)
    {
        if (text is "半") return "30";

        var digits = DigitsPattern.Match(text);
        return digits.Success ? digits.Value : "0";
    }

    // ------------------------------------------------------------------
    // 場所と、読めない言い回し
    // ------------------------------------------------------------------

    private static string? ReadLocation(ref string rest)
    {
        var match = LocationPattern.Match(rest);
        if (!match.Success) return null;

        rest = Replace(rest, match.Value);
        return match.Groups[1].Value;
    }

    private static string? Unsupported(string text)
    {
        foreach (var pattern in UnsupportedPatterns)
        {
            var match = pattern.Match(text);
            if (match.Success) return match.Value.Trim();
        }

        return null;
    }

    private static string Replace(string text, string found) =>
        text.Replace(found, " ", StringComparison.Ordinal);

    /// <summary>
    /// 全角数字（０〜９）を半角に直したうえで整数に読む。
    /// <para>
    /// 正規表現の <c>\d</c> は全角数字にも一致するが、<see cref="int.Parse(string, IFormatProvider)"/> は
    /// 受け付けず <see cref="FormatException"/> になる。読めなければ例外を投げず false を返す
    /// （呼び出し側は既存の日付エラー・時刻エラーの扱いに倒す）。
    /// </para>
    /// <para>ここで受け取るのは正規表現が切り出した数字部分だけなので、題や場所の文字には影響しない。</para>
    /// </summary>
    private static bool TryDigits(string text, out int value)
    {
        Span<char> buffer = text.Length <= 32 ? stackalloc char[text.Length] : new char[text.Length];

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            buffer[i] = c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c;
        }

        return int.TryParse(buffer, NumberStyles.Integer, Invariant, out value);
    }

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private const string WeekdayNames = "日月火水木金土";

    /// <summary>「明日」などの言い方と、基準日からの日数。長いものから見る。</summary>
    private static readonly (string Word, int Offset)[] RelativeDays =
    [
        ("明々後日", 3), ("しあさって", 3), ("明後日", 2), ("あさって", 2),
        ("明日", 1), ("あした", 1), ("今日", 0), ("本日", 0), ("昨日", -1),
    ];

    /// <summary>週の言い方。「再来週」は「来週」を含むので先に見る。</summary>
    private static readonly (string Word, int Offset)[] WeekWords =
    [
        ("再来週", 14), ("来週", 7), ("今週", 0),
    ];

    private const string AmPm = @"(午前|午後)?\s*";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DigitsPattern = new(@"\d{1,2}", RegexOptions.Compiled);
    private static readonly Regex WeekdayPattern = new(@"([日月火水木金土])曜日?", RegexOptions.Compiled);
    private static readonly Regex NextMonthDayPattern = new(@"来月(\d{1,2})日?", RegexOptions.Compiled);
    // 区切りは全角（／・：・－）でも打てるようにしてある。IME を点けたまま数字だけ
    // 全角で打つ、コロンや区切りまでつられて全角になる、のどちらも起きるため
    private static readonly Regex FullDatePattern =
        new(@"(\d{4})[/\-／－年](\d{1,2})[/\-／－月](\d{1,2})日?", RegexOptions.Compiled);
    private static readonly Regex[] MonthDayPatterns =
    [
        new(@"(\d{1,2})月(\d{1,2})日?", RegexOptions.Compiled),
        new(@"(\d{1,2})[/／](\d{1,2})", RegexOptions.Compiled),
    ];
    private static readonly Regex DaysAfterPattern = new(@"(\d{1,2})日後", RegexOptions.Compiled);
    private static readonly Regex DayOfMonthPattern = new(@"(\d{1,2})日(?!間|後)", RegexOptions.Compiled);

    private static readonly Regex ColonRangePattern = new(
        AmPm + @"(\d{1,2})[:：](\d{2})\s*(?:から|[-〜~ー−－])\s*" + AmPm + @"(\d{1,2})[:：](\d{2})", RegexOptions.Compiled);
    private static readonly Regex KanjiRangePattern = new(
        AmPm + @"(\d{1,2})時(?!間)(半|\d{1,2}分?)?\s*(?:から|[-〜~ー−－])\s*" + AmPm + @"(\d{1,2})時(?!間)(半|\d{1,2}分?)?",
        RegexOptions.Compiled);
    private static readonly Regex ColonPattern = new(AmPm + @"(\d{1,2})[:：](\d{2})", RegexOptions.Compiled);
    private static readonly Regex KanjiPattern =
        new(AmPm + @"(\d{1,2})時(?!間)(半|\d{1,2}分?)?", RegexOptions.Compiled);

    private static readonly Regex LocationPattern = new(@"[@＠]\s*(\S+)", RegexOptions.Compiled);

    /// <summary>ここで読めない言い回し。見つけたら登録を止める。</summary>
    private static readonly Regex[] UnsupportedPatterns =
    [
        new(@"毎週|毎日|毎月|毎年", RegexOptions.Compiled),
        new(@"来年|再来年|先週|先月|今月|去年|昨年", RegexOptions.Compiled),
        new(@"\d{1,2}\s*[/月]\s*\d{1,2}日?\s*[-〜~ー−]\s*\d{1,2}", RegexOptions.Compiled),
    ];
}
