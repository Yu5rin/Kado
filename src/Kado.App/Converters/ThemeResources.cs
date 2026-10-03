using System.Windows;

namespace Kado.App.Converters;

/// <summary>
/// 配色の辞書（<c>Application.Resources</c>）から、ブラシなどを引く。
/// <para>
/// コンバーターが <c>Application.TryFindResource</c> で引いていたものを、<b>配色を当て直すまで同じ結果で
/// 使い回す</b>。チップ1つ・マス1つのたびに、辞書の入れ子をたどって引き直さないため。
/// </para>
/// <para>
/// <b>配色を当て直したら <see cref="Invalidate"/> を呼ぶこと</b>（<c>ThemeManager.Apply</c> が、
/// 辞書を入れ替えた直後・バインドを結び直す前に呼ぶ）。呼ばないと、前の配色のブラシが残る。
/// 結び直し（<c>IThemeSensitiveConverter</c>）は、ここが空になってから引き直す。
/// </para>
/// <para>見つからなかったときは覚えない（あとで辞書に載るかもしれない）。画面のスレッドで使う。</para>
/// </summary>
internal static class ThemeResources
{
    private static readonly KeyedCache<object> Cache = new(maxEntries: 256);

    /// <summary>辞書から引く。無ければ null。</summary>
    public static object? Find(string key) =>
        Cache.GetOrCreate(key, static k => Application.Current?.TryFindResource(k), cacheNull: false);

    /// <summary>覚えたものを捨てる。配色を当て直したときに呼ぶ。</summary>
    public static void Invalidate() => Cache.Clear();
}
