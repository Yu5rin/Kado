using Kado.Presentation.Infrastructure;

namespace Kado.Presentation.Tests;

/// <summary>
/// 日付欄の区切り。
/// <para>押したところのまとまりを選んで、そのまま打ち替えられるようにする。</para>
/// </summary>
public class DateSegmentTests
{
    [Theory]
    [InlineData(0, 0, 4)]    // 年の先頭
    [InlineData(2, 0, 4)]    // 年の途中
    [InlineData(3, 0, 4)]    // 年の末尾
    [InlineData(5, 5, 2)]    // 月
    [InlineData(6, 5, 2)]
    [InlineData(8, 8, 2)]    // 日
    [InlineData(9, 8, 2)]
    public void 押したところのまとまりを選ぶ(int caret, int from, int length)
    {
        Assert.Equal((from, length), DateSegment.At("2026/10/01", caret));
    }

    [Fact]
    public void 区切り文字の上なら手前のまとまりに付ける()
    {
        // 「/」を押したときに次の欄へ飛ぶと、行き過ぎたように感じる
        Assert.Equal((0, 4), DateSegment.At("2026/10/01", 4));
        Assert.Equal((5, 2), DateSegment.At("2026/10/01", 7));
    }

    [Fact]
    public void 区切り文字が変わっても同じように読む()
    {
        Assert.Equal((5, 2), DateSegment.At("2026-10-01", 5));
        Assert.Equal((5, 2), DateSegment.At("2026年10月1日", 5));
    }

    [Fact]
    public void 空の欄は選ばない()
    {
        Assert.Equal((0, 0), DateSegment.At(string.Empty, 0));
        Assert.Equal((0, 0), DateSegment.At(null, 3));
        Assert.Equal((0, 0), DateSegment.At("//", 1));
    }

    [Fact]
    public void 範囲の外を指されても落ちない()
    {
        Assert.Equal((8, 2), DateSegment.At("2026/10/01", 99));
        Assert.Equal((0, 4), DateSegment.At("2026/10/01", -5));
    }

    [Theory]
    [InlineData("2026/01/31", 8, 1, "2026/02/01")]    // 月末の日は翌月1日へ
    [InlineData("2026/03/01", 8, -1, "2026/02/28")]   // 月初の日は前月末へ
    [InlineData("2026/12/31", 8, 1, "2027/01/01")]    // 年末は翌年へ
    [InlineData("2026/01/31", 5, 1, "2026/02/28")]    // 月を動かして日が無ければ月末に丸める
    [InlineData("2026/12/15", 5, 1, "2027/01/15")]    // 12月の次は翌年1月
    [InlineData("2026/01/15", 5, -1, "2025/12/15")]
    [InlineData("2024/02/29", 2, 1, "2025/02/28")]    // うるう日の年を動かすと月末へ
    [InlineData("2026年1月31日", 8, 1, "2026年2月1日")]   // 区切りが文字でも日付として動かす
    public void 上下の増減は日付として繰り上げる(string text, int caret, int by, string expected)
    {
        var result = DateSegment.Step(text, caret, by);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value.Text);
    }

    [Fact]
    public void 動かした区切りを選び直せるよう範囲を返す()
    {
        var result = DateSegment.Step("2026/01/31", 8, 1);

        Assert.Equal((8, 2), (result!.Value.From, result.Value.Length));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026/13/01")]   // 日付として読めない
    [InlineData("10/01/2026")]   // 年が先頭でない並びは動かさない
    public void 読めない形は動かさない(string text)
    {
        Assert.Null(DateSegment.Step(text, 0, 1));
    }
}
