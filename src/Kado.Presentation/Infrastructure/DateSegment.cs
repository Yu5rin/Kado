namespace Kado.Presentation.Infrastructure;

/// <summary>
/// 日付の文字列を「年・月・日」の区切りで見る。
/// <para>
/// 押したところのまとまりを選んで、そのまま打ち替えられるようにするのに使う。
/// 区切り文字のあいだを1つのまとまりとして数えるだけなので、書式が
/// <c>2026/10/01</c> でも <c>2026年10月1日</c> でも同じように読める。
/// </para>
/// </summary>
public static class DateSegment
{
    /// <summary>まとまりの区切り。</summary>
    private static readonly char[] Separators = ['/', '-', '.', ' ', '年', '月', '日'];

    /// <summary>
    /// その位置が属するまとまりの範囲。
    /// <para>
    /// 区切り文字の上を指されたら、<b>手前</b>のまとまりに付ける。押した先の欄へ
    /// 飛ぶと、行き過ぎたように感じる。
    /// </para>
    /// </summary>
    /// <returns>始まりの位置と長さ。まとまりが無ければ長さ 0。</returns>
    public static (int From, int Length) At(string? text, int caret)
    {
        if (text is not { Length: > 0 }) return (0, 0);

        var at = Math.Clamp(caret, 0, text.Length - 1);

        if (IsSeparator(text[at]) && at > 0) at--;

        // 区切り文字だけの並びを指された
        if (IsSeparator(text[at])) return (0, 0);

        var from = at;
        while (from > 0 && !IsSeparator(text[from - 1])) from--;

        var to = at;
        while (to < text.Length - 1 && !IsSeparator(text[to + 1])) to++;

        return (from, to - from + 1);
    }

    private static bool IsSeparator(char value) => Array.IndexOf(Separators, value) >= 0;

    /// <summary>
    /// 選んでいる区切りを、日付として1つ動かす。
    /// <para>
    /// 欄の数字だけを足し引きすると、31 の次が 32、12 月の次が 13 月になる。
    /// 日付として <c>AddDays</c>・<c>AddMonths</c>・<c>AddYears</c> するので、
    /// 31 日の次は翌月 1 日、月を動かして日が無ければ月末に丸める。
    /// 並びは「年・月・日」の順（先頭が4桁の年）だけを扱う。
    /// </para>
    /// </summary>
    /// <returns>
    /// 書き換えた文字と、動かした区切りの範囲。読めない形や範囲外なら null。
    /// </returns>
    public static (string Text, int From, int Length)? Step(string? text, int caret, int by)
    {
        if (text is not { Length: > 0 }) return null;

        // 区切りごとの範囲を左から集める
        var spans = new List<(int From, int Length)>();
        var index = 0;
        while (index < text.Length)
        {
            if (IsSeparator(text[index])) { index++; continue; }

            var from = index;
            while (index < text.Length && !IsSeparator(text[index])) index++;
            spans.Add((from, index - from));
        }

        if (spans.Count < 3) return null;

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(text.AsSpan(spans[i].From, spans[i].Length), out numbers[i])) return null;
        }

        // 先頭が4桁でなければ年が先頭とは限らない。決めつけずに動かさない
        if (spans[0].Length != 4) return null;

        var (at, length) = At(text, caret);
        if (length <= 0) return null;

        var target = spans.FindIndex(x => x.From == at);
        if (target is < 0 or > 2) return null;

        DateTime moved;
        try
        {
            var current = new DateTime(numbers[0], numbers[1], numbers[2]);
            moved = target switch
            {
                0 => current.AddYears(by),
                1 => current.AddMonths(by),
                _ => current.AddDays(by),
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        var parts = new[] { moved.Year, moved.Month, moved.Day };
        var builder = new System.Text.StringBuilder();
        var cursor = 0;
        var selection = (From: 0, Length: 0);

        for (var i = 0; i < 3; i++)
        {
            builder.Append(text, cursor, spans[i].From - cursor);

            // 「2026/01/05」は2桁に揃え、「2026年1月5日」は揃えない
            var width = i == 0 ? 4 : (text.Contains('年') ? 1 : 2);
            var digits = parts[i].ToString(System.Globalization.CultureInfo.InvariantCulture)
                .PadLeft(width, '0');
            if (i == target) selection = (builder.Length, digits.Length);

            builder.Append(digits);
            cursor = spans[i].From + spans[i].Length;
        }

        builder.Append(text, cursor, text.Length - cursor);

        return (builder.ToString(), selection.From, selection.Length);
    }
}
