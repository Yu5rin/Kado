using Kado.App.Converters;

namespace Kado.Shell.Tests;

/// <summary>
/// コンバーターが色のブラシや辞書の引き当てを使い回す入れ物。
/// チップ1つ・マス1つのたびに、同じ結果になる仕事をやり直さない。
/// </summary>
public class KeyedCacheTests
{
    [Fact]
    public void 同じ鍵は作り直さず_同じものを返す()
    {
        var cache = new KeyedCache<object>();
        var made = 0;

        var first = cache.GetOrCreate("#ff0000", _ => { made++; return new object(); });
        var second = cache.GetOrCreate("#ff0000", _ => { made++; return new object(); });

        Assert.Same(first, second);
        Assert.Equal(1, made);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void 鍵が違えば別のものを作る_大文字小文字も別の鍵()
    {
        var cache = new KeyedCache<object>();

        var a = cache.GetOrCreate("#FF0000", _ => new object());
        var b = cache.GetOrCreate("#ff0000", _ => new object());

        Assert.NotSame(a, b);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void 作れなかった結果も覚える_壊れた色で例外を毎回投げさせない()
    {
        var cache = new KeyedCache<object>();
        var attempts = 0;

        for (var i = 0; i < 100; i++)
        {
            Assert.Null(cache.GetOrCreate("壊れた色", _ => { attempts++; return null; }));
        }

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void 作れなかった結果を覚えない指定なら_次も作りに行く()
    {
        // 配色の辞書に、あとから載るかもしれないもの
        var cache = new KeyedCache<object>();
        var attempts = 0;
        object? available = null;

        Assert.Null(cache.GetOrCreate("AccentBrush", _ => { attempts++; return available; }, cacheNull: false));

        available = new object();
        Assert.Same(available, cache.GetOrCreate("AccentBrush", _ => { attempts++; return available; }, cacheNull: false));

        // 載ったあとは覚える
        cache.GetOrCreate("AccentBrush", _ => { attempts++; return available; }, cacheNull: false);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public void 忘れさせると_次は作り直す_配色を当て直したとき()
    {
        var cache = new KeyedCache<object>();
        var theme = "light";

        var before = cache.GetOrCreate("AccentBrush", _ => theme);
        theme = "dark";

        // 当て直す前は、前の配色のまま（使い回している）
        Assert.Equal("light", cache.GetOrCreate("AccentBrush", _ => theme));

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal("dark", cache.GetOrCreate("AccentBrush", _ => theme));
        Assert.Equal("light", before);
    }

    [Fact]
    public void 件数の上限に達したら空にして続ける()
    {
        var cache = new KeyedCache<object>(maxEntries: 8);

        for (var i = 0; i < 100; i++) cache.GetOrCreate($"#{i:X6}", _ => new object());

        Assert.True(cache.Count <= 8);

        // 上限を超えても、結果そのものは正しく返る
        Assert.Equal("x", cache.GetOrCreate("新しい鍵", _ => "x"));
    }

    [Fact]
    public void 複数のスレッドから同時に使っても_残るのは1つで全員が同じものを受け取る()
    {
        var cache = new KeyedCache<object>();
        var results = new object?[64];

        Parallel.For(0, results.Length, i => results[i] = cache.GetOrCreate("#336699", _ => new object()));

        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(1, cache.Count);
        Assert.Same(results[0], cache.GetOrCreate("#336699", _ => new object()));
    }
}
