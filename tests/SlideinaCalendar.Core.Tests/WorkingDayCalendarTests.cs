using SlideinaCalendar.Core.WorkingDays;

namespace SlideinaCalendar.Core.Tests;

public class WorkingDayCalendarTests
{
    private static readonly WorkingDayCalendar Sut = TestCalendars.September2026;

    // ------------------------------------------------------------------
    // 稼働判定
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1)]   // 火
    [InlineData(4)]   // 金
    [InlineData(24)]  // 木
    [InlineData(30)]  // 水
    public void 登録された日は稼働日(int day) =>
        Assert.True(Sut.IsWorkingDay(new DateOnly(2026, 9, day)));

    [Theory]
    [InlineData(5)]   // 土
    [InlineData(6)]   // 日
    [InlineData(21)]  // 敬老の日
    [InlineData(23)]  // 秋分の日
    public void 登録されていない日は稼働日ではない(int day) =>
        Assert.False(Sut.IsWorkingDay(new DateOnly(2026, 9, day)));

    // ------------------------------------------------------------------
    // データ範囲
    // ------------------------------------------------------------------

    [Fact]
    public void 登録範囲の内側はデータありとして扱う()
    {
        Assert.True(Sut.HasDataFor(new DateOnly(2026, 9, 1)));
        Assert.True(Sut.HasDataFor(new DateOnly(2026, 9, 30)));
        // 範囲内なら非稼働日でも「データはある」
        Assert.True(Sut.HasDataFor(new DateOnly(2026, 9, 5)));
    }

    [Fact]
    public void 登録範囲の外側はデータなしとして扱う()
    {
        Assert.False(Sut.HasDataFor(new DateOnly(2026, 8, 31)));
        Assert.False(Sut.HasDataFor(new DateOnly(2026, 10, 1)));
    }

    [Fact]
    public void 空のカレンダーはどの日もデータなし()
    {
        Assert.False(WorkingDayCalendar.Empty.HasDataFor(new DateOnly(2026, 9, 1)));
        Assert.False(WorkingDayCalendar.Empty.IsWorkingDay(new DateOnly(2026, 9, 1)));
        Assert.Equal(0, WorkingDayCalendar.Empty.Count);
        Assert.Null(WorkingDayCalendar.Empty.RangeStart);
    }

    // ------------------------------------------------------------------
    // 月内の通し番号と集計
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(7, 5)]    // 土日をまたいでも通し番号は連続する
    [InlineData(18, 14)]
    [InlineData(24, 15)]  // 3連休明け
    [InlineData(25, 16)]
    [InlineData(30, 19)]
    public void その月の何実働日目かを返す(int day, int expected) =>
        Assert.Equal(expected, Sut.IndexInMonth(new DateOnly(2026, 9, day)));

    [Fact]
    public void 非稼働日の通し番号はnull() =>
        Assert.Null(Sut.IndexInMonth(new DateOnly(2026, 9, 21)));

    [Fact]
    public void 範囲外の通し番号はnull() =>
        Assert.Null(Sut.IndexInMonth(new DateOnly(2026, 10, 1)));

    [Fact]
    public void 月の実働日数を数える()
    {
        Assert.Equal(19, Sut.CountInMonth(2026, 9));
        Assert.Equal(0, Sut.CountInMonth(2026, 10));
    }

    [Fact]
    public void 月が丸ごと登録範囲に入っているかを判定できる()
    {
        Assert.True(Sut.IsMonthFullyCovered(2026, 9));
        Assert.False(Sut.IsMonthFullyCovered(2026, 10));
    }

    [Fact]
    public void 月の残り実働日数は今日を数えない()
    {
        // 9/24 の後は 25・28・29・30 の 4 日
        Assert.Equal(4, Sut.RemainingInMonth(new DateOnly(2026, 9, 24)));
        Assert.Equal(18, Sut.RemainingInMonth(new DateOnly(2026, 9, 1)));
        Assert.Equal(0, Sut.RemainingInMonth(new DateOnly(2026, 9, 30)));

        // 数えるのはあくまで「その日が属する月」の残り。8月には稼働日が無いので 0
        Assert.Equal(0, Sut.RemainingInMonth(new DateOnly(2026, 8, 31)));
    }

    [Fact]
    public void 月の途中から始まるデータでは通し番号が登録範囲だけで数えられる()
    {
        // 9/10 から登録。9/10 が 1 実働日目になる
        var partial = WorkingDayCalendar.Create(
            TestCalendars.September2026WorkingDays.Where(d => d.Day >= 10));

        Assert.Equal(1, partial.IndexInMonth(new DateOnly(2026, 9, 10)));
        Assert.False(partial.IsMonthFullyCovered(2026, 9));
    }

    // ------------------------------------------------------------------
    // マイルストーン
    // ------------------------------------------------------------------

    [Fact]
    public void 指定日のマイルストーンを引ける()
    {
        var found = Sut.MilestonesOn(new DateOnly(2026, 9, 14));
        Assert.Equal("仕様期限", Assert.Single(found).Name);
    }

    [Fact]
    public void マイルストーンが無い日は空を返す() =>
        Assert.Empty(Sut.MilestonesOn(new DateOnly(2026, 9, 1)));

    [Fact]
    public void 同じ日に複数のマイルストーンを登録できる()
    {
        var date = new DateOnly(2026, 9, 14);
        var calendar = WorkingDayCalendar.Create(
            TestCalendars.September2026WorkingDays,
            [new Milestone(date, "仕様期限"), new Milestone(date, "1次GO")]);

        Assert.Equal(2, calendar.MilestonesOn(date).Count);
    }

    [Fact]
    public void マイルストーンの登録範囲は稼働日とは別に持つ()
    {
        Assert.Equal(new DateOnly(2026, 9, 1), Sut.RangeStart);
        Assert.Equal(new DateOnly(2026, 9, 30), Sut.RangeEnd);
        Assert.Equal(new DateOnly(2026, 9, 14), Sut.MilestoneRangeStart);
        Assert.Equal(new DateOnly(2026, 9, 17), Sut.MilestoneRangeEnd);

        Assert.True(Sut.HasMilestoneDataFor(new DateOnly(2026, 9, 15)));
        Assert.False(Sut.HasMilestoneDataFor(new DateOnly(2026, 9, 1)));
    }
}
