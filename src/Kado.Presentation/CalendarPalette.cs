namespace Kado.Presentation;

/// <summary>
/// カレンダーとタスクリストに割り当てる色。
/// <para>
/// 予定の色は所属カレンダーで決まる。Google も既定はカレンダー色で、
/// 予定ごとの上書きは任意という作りになっている。
/// </para>
/// </summary>
public static class CalendarPalette
{
    /// <summary>選べる色。モックの左パネルで使っている並び。</summary>
    public static readonly IReadOnlyList<string> Colors =
    [
        "#27528f",   // 藍
        "#2f7d5b",   // 緑
        "#c2762b",   // 琥珀
        "#8f5fa8",   // 紫
        "#3b6ea8",   // 青
        "#b03a34",   // 赤
        "#4a7c7e",   // 青緑
        "#7a6a52",   // 茶
    ];

    /// <summary>名前に対応する色の呼び名。選ぶときに出す。</summary>
    public static readonly IReadOnlyList<string> Names =
        ["藍", "緑", "琥珀", "紫", "青", "赤", "青緑", "茶"];

    /// <summary>
    /// 名前から色を決める。取り込んだ色が無いときに使う。
    /// <para>
    /// <c>string.GetHashCode</c> は実行ごとに変わるので使えない。同じ名前には
    /// 常に同じ色が付くようにする。
    /// </para>
    /// </summary>
    public static string ColorFor(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var hash = 17;
        foreach (var c in name) hash = unchecked(hash * 31 + c);

        return Colors[Math.Abs(hash % Colors.Count)];
    }

    /// <summary>次に作るときの色。すでに使われている色を避ける。</summary>
    public static string NextColor(IEnumerable<string?> used)
    {
        ArgumentNullException.ThrowIfNull(used);

        var taken = used.Where(c => c is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Colors.FirstOrDefault(c => !taken.Contains(c)) ?? Colors[0];
    }
}
