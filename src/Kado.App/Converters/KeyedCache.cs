using System.Collections.Concurrent;

namespace Kado.App.Converters;

/// <summary>
/// 文字列の鍵から作ったものを、作り直さずに使い回す入れ物。
/// <para>
/// コンバーターは、チップ1つ・マス1つのたびに呼ばれる。色の文字列から色を解いてブラシを作る、
/// 辞書からブラシを引く、といった同じ結果になる仕事を毎回やり直さないために使う
/// （<c>BrushCache</c>・<c>ThemeResources</c>）。WPF に依存しない（Linux のテストにそのまま取り込める）。
/// </para>
/// <para>
/// <b>どのスレッドから呼んでもよい。</b>同時に初めて呼ばれて2回作ることがあっても、残るのは1つ。
/// 入れ物が <paramref name="maxEntries"/> に達したら空にして作り直す（鍵が際限なく増えても、
/// 居座らない）。<b>作れなかった（null）結果も覚える</b>（<c>cacheNull</c> が true のとき）。
/// 壊れた色の文字列で、例外を毎回投げさせないため。
/// </para>
/// </summary>
internal sealed class KeyedCache<T> where T : class
{
    private readonly ConcurrentDictionary<string, Entry> _items = new(StringComparer.Ordinal);
    private readonly int _maxEntries;

    public KeyedCache(int maxEntries = 512) => _maxEntries = Math.Max(1, maxEntries);

    /// <summary>いま覚えている件数。</summary>
    public int Count => _items.Count;

    /// <summary>
    /// 覚えていればそれを、無ければ <paramref name="create"/> で作って覚えて返す。
    /// </summary>
    /// <param name="cacheNull">作れなかった（null）ことも覚えるか。覚えると、次からは作りに行かない。</param>
    public T? GetOrCreate(string key, Func<string, T?> create, bool cacheNull = true)
    {
        if (_items.TryGetValue(key, out var hit)) return hit.Value;

        var value = create(key);

        if (value is null && !cacheNull) return null;

        // 満杯なら空にする。1つずつ追い出すほどの数ではない
        if (_items.Count >= _maxEntries) _items.Clear();

        return _items.GetOrAdd(key, new Entry(value)).Value;
    }

    /// <summary>全部忘れる。元になるものが変わったとき（配色を当て直したとき）に呼ぶ。</summary>
    public void Clear() => _items.Clear();

    private sealed record Entry(T? Value);
}
