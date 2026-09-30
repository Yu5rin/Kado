using Kado.Presentation.Update;

namespace Kado.Presentation.Tests;

/// <summary>
/// Atom フィード（<c>releases.atom</c>）からの最新タグの読み取り。
/// <para>
/// 応答は途中の中継に差し替えられうる。読み方を絞ってあること（外部実体を読まない、大きさに上限、
/// <c>vX.Y.Z</c> 以外は無視）を固定する。
/// </para>
/// </summary>
public class AtomFeedTests
{
    /// <summary>GitHub の <c>releases.atom</c> と同じ形。実物から要らない部分を削ったもの。</summary>
    internal static string Feed(params string[] tags)
    {
        var entries = string.Concat(tags.Select(t => $"""
              <entry>
                <id>tag:github.com,2008:Repository/1000000/{t}</id>
                <updated>2026-09-20T00:00:00Z</updated>
                <link rel="alternate" type="text/html" href="https://github.com/Yu5rin/Kado/releases/tag/{t}"/>
                <title>{t}</title>
                <content type="html">&lt;p&gt;同期を直しました&lt;/p&gt;</content>
              </entry>
            """));

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom" xml:lang="en-US">
              <id>tag:github.com,2008:https://github.com/Yu5rin/Kado/releases</id>
              <link type="text/html" rel="alternate" href="https://github.com/Yu5rin/Kado/releases"/>
              <title>Release notes from Kado</title>
            {entries}
            </feed>
            """;
    }

    [Fact]
    public void 実物と同じ形のフィードから最新のタグを取れる()
    {
        Assert.Equal("v1.0.8", AtomFeed.ReadLatestTag(Feed("v1.0.8", "v1.0.7", "v1.0.6")));
    }

    [Fact]
    public void 並びが新しい順でなくても最大の版を選ぶ()
    {
        // 並びに頼ると、並びが変わったときに古い版を「最新」と判断してしまう
        Assert.Equal("v1.0.8", AtomFeed.ReadLatestTag(Feed("v1.0.6", "v1.0.8", "v1.0.7")));

        // 文字で比べると 1.0.10 が 1.0.9 より古くなる。数として比べる
        Assert.Equal("v1.0.10", AtomFeed.ReadLatestTag(Feed("v1.0.9", "v1.0.10")));
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("v1.0.0-beta")]     // プレリリースの表記
    [InlineData("v1.0.0+build7")]
    [InlineData("1.0.0")]           // v が無い
    [InlineData("v1.0")]            // 数字が3つではない
    [InlineData("v1.0.0.1")]
    [InlineData("v99999999999.0.0")]
    public void タグの表記がvXYZ以外なら無視する(string tag)
    {
        // v1.0.7 は読める。それ以外が混ざっていても、読める中での最大を返す
        Assert.Equal("v1.0.7", AtomFeed.ReadLatestTag(Feed(tag, "v1.0.7")));

        // 読めるものが無ければ null
        Assert.Null(AtomFeed.ReadLatestTag(Feed(tag)));
    }

    [Fact]
    public void リリースが一件も無ければnull()
    {
        Assert.Null(AtomFeed.ReadLatestTag(Feed()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("<feed>閉じていない")]
    [InlineData("これは XML ではない")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    public void 壊れた応答でも落ちずにnullを返す(string? xml)
    {
        // 配布元が一時的におかしな応答を返しても、呼び出し側は API で確かめ直せる
        Assert.Null(AtomFeed.ReadLatestTag(xml));
    }

    [Fact]
    public void DTD付きの応答は読まない()
    {
        // 外部実体（XXE）で、手元のファイルを読ませたり、外へ取りに行かせたりしない。
        // DOCTYPE があるだけで読めないものとして捨てる
        var xxe = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE feed [ <!ENTITY secret SYSTEM "file:///etc/passwd"> ]>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <link rel="alternate" href="https://github.com/Yu5rin/Kado/releases/tag/v9.9.9"/>
                <title>&secret;</title>
              </entry>
            </feed>
            """;

        Assert.Null(AtomFeed.ReadLatestTag(xxe));
    }

    [Fact]
    public void 実体を使わないDOCTYPEでも読まない()
    {
        // 実体を参照していなくても、DTD があれば読まない。「DTD は読み飛ばす」設定に
        // 緩めたときに気づけるように、本文は正しい形にしてある
        var withDoctype = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE feed []>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <link rel="alternate" href="https://github.com/Yu5rin/Kado/releases/tag/v1.0.8"/>
              </entry>
            </feed>
            """;

        Assert.Null(AtomFeed.ReadLatestTag(withDoctype));
    }

    [Fact]
    public void 内部実体の展開で膨らませる応答も読まない()
    {
        // いわゆる billion laughs。DTD を禁止しているので、そもそも展開に入らない
        var bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE feed [
              <!ENTITY a "aaaaaaaaaa">
              <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">
              <!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
            ]>
            <feed xmlns="http://www.w3.org/2005/Atom"><title>&c;</title></feed>
            """;

        Assert.Null(AtomFeed.ReadLatestTag(bomb));
    }

    [Fact]
    public void 大きすぎる応答は読まない()
    {
        // 中身が正しくても、上限を超えたものは受け付けない
        var padding = new string('x', AtomFeed.MaxBytes);
        var big = Feed("v1.0.8").Replace("<title>Release notes from Kado</title>", $"<title>{padding}</title>");

        Assert.True(big.Length > AtomFeed.MaxBytes);
        Assert.Null(AtomFeed.ReadLatestTag(big));
    }

    [Fact]
    public void linkが無いentryは飛ばす()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><title>v9.9.9</title></entry>
              <entry>
                <link rel="alternate" type="text/html" href="https://github.com/Yu5rin/Kado/releases/tag/v1.0.8"/>
              </entry>
            </feed>
            """;

        // タグ名は link の href から取る。title だけの entry は当てにしない
        Assert.Equal("v1.0.8", AtomFeed.ReadLatestTag(xml));
    }
}
