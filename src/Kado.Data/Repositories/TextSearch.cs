using System.Text;

namespace Kado.Data.Repositories;

/// <summary>
/// 題・場所・メモの文字探し。<b>大文字小文字を区別せず（<see cref="StringComparison.OrdinalIgnoreCase"/>）、
/// 全角と半角は別の文字として扱う。</b>
/// <para>
/// 以前は全予定・全タスクを読み込んでから、この比べ方で絞っていた。いまは SQL の <c>LIKE</c> で
/// 先に絞り、残ったものにだけこの比べ方を当てる。<c>LIKE</c> は ASCII の大文字小文字しか
/// 区別せずに済ませないので、そのままでは今の探し方と結果が食い違う（全角の「Ａ」と「ａ」など）。
/// そこで、<b>LIKE で絞ってよい文字だけのとき</b>（<see cref="TryCreateLikePattern"/>）に限って先に絞り、
/// そうでなければ絞らず全部に比べ方を当てる。どちらでも結果は同じ。
/// </para>
/// </summary>
public static class TextSearch
{
    /// <summary><c>LIKE</c> のエスケープに使う文字。SQL 側は <c>ESCAPE '\'</c> と書いている。</summary>
    public const char LikeEscape = '\\';

    /// <summary>
    /// 今までの比べ方。<paramref name="value"/> が <paramref name="text"/> を含むか。
    /// 空・null は含まない。
    /// </summary>
    public static bool Contains(string? value, string text) =>
        value is { Length: > 0 } && value.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>LIKE ... ESCAPE '\'</c> に渡すパターンを作る。作れないとき（先に絞ってはいけないとき）は false。
    /// <para>
    /// 絞ってよいのは、<b>どの文字も、大文字小文字の別を持たないか、ASCII の英字のとき</b>。
    /// SQLite の <c>LIKE</c> が大文字小文字を同一視するのは ASCII だけなので、全角の英字や
    /// アクセント付きの文字（Ａ・é など）を含むときは、<c>LIKE</c> が今より多くを落としてしまう。
    /// サロゲートペア（絵文字や古い文字）も、大文字小文字を持つ文字があるので絞らない。
    /// 日本語（ひらがな・カタカナ・漢字・全角の記号や数字）は大文字小文字が無いので絞ってよい。
    /// </para>
    /// <para><c>%</c>・<c>_</c>・<c>\</c> は文字として探すので、エスケープする。</para>
    /// </summary>
    public static bool TryCreateLikePattern(string text, out string pattern)
    {
        pattern = string.Empty;

        if (text.Length == 0) return false;

        var builder = new StringBuilder(text.Length + 4).Append('%');

        foreach (var c in text)
        {
            if (!IsSafeForLike(c)) return false;

            if (c is '%' or '_' or LikeEscape) builder.Append(LikeEscape);
            builder.Append(c);
        }

        pattern = builder.Append('%').ToString();
        return true;
    }

    /// <summary>
    /// この文字を <c>LIKE</c> で比べて、<see cref="StringComparison.OrdinalIgnoreCase"/> と食い違わないか。
    /// </summary>
    internal static bool IsSafeForLike(char c)
    {
        // ASCII は LIKE も大文字小文字を同一視する。NUL は C の文字列の終わりになるので避ける
        if (c < 0x80) return c != '\0';

        if (char.IsSurrogate(c)) return false;

        // 大文字小文字の別を持つ文字は、LIKE では同一視されない
        return char.ToUpperInvariant(c) == c && char.ToLowerInvariant(c) == c;
    }
}
