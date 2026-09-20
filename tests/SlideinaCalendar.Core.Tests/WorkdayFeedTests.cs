using SlideinaCalendar.Core.Import;
using SlideinaCalendar.Core.WorkingDays;

namespace SlideinaCalendar.Core.Tests;

/// <summary>配信用の実働日データ（feed.json）の読み書き。</summary>
public class WorkdayFeedTests
{
    private const string Sample = """
        {
          "app": "inaCalendar", "type": "workingdays", "updatedAt": "2026-09-20",
          "workingDays": ["2026-09-01", "2026-09-02", "2026-09-03"],
          "dataStart": "2026-09-01", "dataEnd": "2026-09-30",
          "milestones": [{ "date": "2026-09-14", "name": "仕様期限" }]
        }
        """;

    [Fact]
    public void 稼働日と期間を読む()
    {
        var result = WorkdayFeed.Read(Sample);

        Assert.Equal(3, result.WorkingDays.Count);
        Assert.Equal(new DateOnly(2026, 9, 1), result.WorkingDayRangeStart);
        Assert.Equal(new DateOnly(2026, 9, 30), result.WorkingDayRangeEnd);
    }

    [Fact]
    public void マイルストーンも読む()
    {
        var result = WorkdayFeed.Read(Sample);

        Assert.Equal("仕様期限", result.Milestones.Single().Name);
        Assert.Equal(new DateOnly(2026, 9, 14), result.MilestoneRangeStart);
    }

    [Fact]
    public void マイルストーンが無くても読める()
    {
        var result = WorkdayFeed.Read("""
            { "type": "workingdays", "workingDays": ["2026-09-01"] }
            """);

        Assert.Single(result.WorkingDays);
        Assert.Empty(result.Milestones);

        // 期間が書かれていなければ稼働日の両端を使う
        Assert.Equal(new DateOnly(2026, 9, 1), result.WorkingDayRangeStart);
    }

    [Fact]
    public void 読めない日付は飛ばして警告にする()
    {
        var result = WorkdayFeed.Read("""
            { "type": "workingdays", "workingDays": ["2026-09-01", "きのう"] }
            """);

        Assert.Single(result.WorkingDays);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void 稼働日が無ければ受け取らない()
    {
        // 稼働日が皆無のファイルは実働日データとして成立しない
        Assert.Throws<InvalidDataException>(() =>
            WorkdayFeed.Read("""{ "type": "workingdays", "workingDays": [] }"""));
    }

    [Fact]
    public void JSONでなければ受け取らない()
    {
        Assert.Throws<InvalidDataException>(() => WorkdayFeed.Read("実働日"));
    }

    [Fact]
    public void 書き出して読み直すと同じになる()
    {
        var calendar = TestCalendars.September2026;

        var json = WorkdayFeed.Write(calendar, new DateOnly(2026, 9, 20));
        var back = WorkdayFeed.Read(json);

        Assert.Equal(calendar.Days.Count, back.WorkingDays.Count);
        Assert.Equal(calendar.RangeStart, back.WorkingDayRangeStart);
        Assert.Equal(calendar.RangeEnd, back.WorkingDayRangeEnd);
        Assert.Equal(calendar.AllMilestones.Count, back.Milestones.Count);
    }

    [Fact]
    public void 空のカレンダーは書き出さない()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WorkdayFeed.Write(WorkingDayCalendar.Empty, new DateOnly(2026, 9, 20)));
    }
}
