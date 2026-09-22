using Kado.Core.Import;
using Kado.Core.WorkingDays;

namespace Kado.Core.Tests;

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
    public void 型が違う要素は例外にせず読み飛ばす()
    {
        // 数値要素に GetValue<string>() をそのまま使うと例外になる。1件おかしいだけで
        // 取り込み全体を止めないことを確かめる
        var result = WorkdayFeed.Read("""
            { "type": "workingdays", "workingDays": ["2026-09-01", 20260902, null, true] }
            """);

        Assert.Single(result.WorkingDays);
        Assert.Equal(new DateOnly(2026, 9, 1), result.WorkingDays[0]);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void マイルストーンの名前が長すぎると読み飛ばす()
    {
        var longName = new string('あ', 300);
        var json = $$"""
            {
              "type": "workingdays", "workingDays": ["2026-09-01"],
              "milestones": [
                { "date": "2026-09-14", "name": "{{longName}}" },
                { "date": "2026-09-15", "name": "短い名前" }
              ]
            }
            """;

        var result = WorkdayFeed.Read(json);

        Assert.Single(result.Milestones);
        Assert.Equal("短い名前", result.Milestones[0].Name);
        Assert.Contains(result.Warnings, w => w.Contains("長すぎる"));
    }

    [Fact]
    public void 稼働日の件数が上限を超えたぶんは読み飛ばす()
    {
        var days = string.Join(",", Enumerable.Range(1, 10_050)
            .Select(i => $"\"{new DateOnly(2020, 1, 1).AddDays(i):yyyy-MM-dd}\""));
        var json = $$"""{ "type": "workingdays", "workingDays": [{{days}}] }""";

        var result = WorkdayFeed.Read(json);

        Assert.Equal(10_000, result.WorkingDays.Count);
        Assert.Contains(result.Warnings, w => w.Contains("上限"));
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
