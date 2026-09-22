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
}
