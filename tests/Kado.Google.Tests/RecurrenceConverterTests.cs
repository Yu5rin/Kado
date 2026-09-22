using Kado.Core.Recurrence;
using Kado.Google.Mapping;

namespace Kado.Google.Tests;

/// <summary>
/// 繰り返しの書き方の行き来。
/// <para>ここを取りこぼすと、同期のたびに繰り返しが壊れる。</para>
/// </summary>
public class RecurrenceConverterTests
{
    [Fact]
    public void 単純な繰り返しを読める()
    {
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", RecurrenceConverter.FromGoogle(["RRULE:FREQ=WEEKLY;BYDAY=MO"]));
    }

    [Fact]
    public void 除外日を同じ文字列に混ぜる()
    {
        var spec = RecurrenceConverter.FromGoogle(
            ["RRULE:FREQ=WEEKLY;BYDAY=MO", "EXDATE;TZID=Asia/Tokyo:20260921T090000"]);

        Assert.Equal("FREQ=WEEKLY;BYDAY=MO;EXDATE=20260921", spec);

        // Core が読めることまで確かめる
        var rule = RecurrenceRule.Parse(spec!);
        Assert.Contains(new DateOnly(2026, 9, 21), rule.ExceptDates);
    }

    [Fact]
    public void 除外日が複数でも読める()
    {
        var spec = RecurrenceConverter.FromGoogle(
            ["RRULE:FREQ=DAILY", "EXDATE;VALUE=DATE:20260921,20260922"]);

        Assert.Equal("FREQ=DAILY;EXDATE=20260921,20260922", spec);
    }

    [Fact]
    public void 除外日の行が分かれていても読める()
    {
        var spec = RecurrenceConverter.FromGoogle(
            ["RRULE:FREQ=DAILY", "EXDATE;VALUE=DATE:20260921", "EXDATE;VALUE=DATE:20260922"]);

        Assert.Equal("FREQ=DAILY;EXDATE=20260921,20260922", spec);
    }

    [Theory]
    [InlineData("RDATE;VALUE=DATE:20260921")]
    [InlineData("EXRULE:FREQ=MONTHLY")]
    public void 表せない行があれば読み取らない(string extra)
    {
        // 半端に読むと、書き戻しのときに落としてしまう。
        // 控えた生データは残るので、patch で recurrence を送らなければ Google 側は無傷
        Assert.Null(RecurrenceConverter.FromGoogle(["RRULE:FREQ=DAILY", extra]));
    }

    [Fact]
    public void RRULEが2本あれば読み取らない()
    {
        Assert.Null(RecurrenceConverter.FromGoogle(["RRULE:FREQ=DAILY", "RRULE:FREQ=WEEKLY"]));
    }

    [Fact]
    public void 繰り返しが無ければnull()
    {
        Assert.Null(RecurrenceConverter.FromGoogle([]));
    }

    // ------------------------------------------------------------------
    // 書き出し
    // ------------------------------------------------------------------

    [Fact]
    public void 書き出すとRRULEの行になる()
    {
        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=MO"], RecurrenceConverter.ToGoogle("FREQ=WEEKLY;BYDAY=MO"));
    }

    [Fact]
    public void 除外日は別の行に分ける()
    {
        Assert.Equal(
            ["RRULE:FREQ=WEEKLY;BYDAY=MO", "EXDATE;VALUE=DATE:20260921"],
            RecurrenceConverter.ToGoogle("FREQ=WEEKLY;BYDAY=MO;EXDATE=20260921"));
    }

    [Fact]
    public void 繰り返しを外すと空の配列になる()
    {
        // Google では空の配列が「繰り返しを外す」意味になる
        Assert.Empty(RecurrenceConverter.ToGoogle(null));
        Assert.Empty(RecurrenceConverter.ToGoogle("   "));
    }

    [Fact]
    public void 接頭辞が付いていても書き出せる()
    {
        Assert.Equal(["RRULE:FREQ=DAILY"], RecurrenceConverter.ToGoogle("RRULE:FREQ=DAILY"));
    }

    [Theory]
    [InlineData("FREQ=DAILY")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE,FR")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=24")]
    [InlineData("FREQ=YEARLY;BYMONTH=9;BYMONTHDAY=24")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=TH;EXDATE=20260921,20260928")]
    public void 往復しても変わらない(string spec)
    {
        // 同期のたびに書き方が揺れると、差分が出続けて書き戻しが止まらない
        Assert.Equal(spec, RecurrenceConverter.FromGoogle(RecurrenceConverter.ToGoogle(spec)));
    }
}
