using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.Tests;

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
}
