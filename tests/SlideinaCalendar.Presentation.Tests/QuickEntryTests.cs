using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>1行から予定を入れる。</summary>
public class QuickEntryTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    private static MainViewModel Create(TestWorkspace test) => new(test.Workspace, Today);

    [Fact]
    public void 一行で予定が入る()
    {
        using var test = TestWorkspace.Create();
        var main = Create(test);

        main.QuickText = "明日15時 打合せ @会議室A";
        main.QuickCommand.Execute(null);

        var added = test.Workspace.Events.All().Single(e => e.Title == "打合せ");
        Assert.Equal(new DateOnly(2026, 9, 25), added.Date);
        Assert.Equal(new TimeOnly(15, 0), added.StartTime);
        Assert.Equal(new TimeOnly(16, 0), added.EndTime);
        Assert.Equal("会議室A", added.Location);
    }

    [Fact]
    public void 入れたら欄は空になりその日へ移る()
    {
        using var test = TestWorkspace.Create();
        var main = Create(test);

        main.QuickText = "明日 打合せ";
        main.QuickCommand.Execute(null);

        Assert.Equal(string.Empty, main.QuickText);
        Assert.Equal(new DateOnly(2026, 9, 25), main.SelectedDate);
    }

    [Fact]
    public void 実働日データの入れ先には入れない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.CreateCalendar(CalendarWorkspace.WorkingDayCalendarName);

        var main = Create(test);
        main.QuickText = "打合せ";
        main.QuickCommand.Execute(null);

        var added = test.Workspace.Events.All().Single(e => e.Title == "打合せ");
        var ina = test.Workspace.Sources.Calendars()
            .Single(c => c.DisplayName == CalendarWorkspace.WorkingDayCalendarName);

        // 次の取り込みで消える場所に置いてはいけない
        Assert.NotEqual(ina.Id, added.CalendarId);
    }

    [Fact]
    public void 読めない言い回しは入れない()
    {
        using var test = TestWorkspace.Create();
        var main = Create(test);

        main.QuickText = "毎週月曜 定例";

        Assert.False(main.CanCommitQuick);
        Assert.Contains("毎週", main.QuickPreview);

        main.QuickCommand.Execute(null);
        Assert.Empty(test.Workspace.Events.All().Where(e => e.Title.Contains("定例")));
    }

    [Fact]
    public void 打つ前に読んだ内容を見せる()
    {
        using var test = TestWorkspace.Create();
        var main = Create(test);

        main.QuickText = "明日15時 打合せ @会議室A";

        Assert.Contains("9/25", main.QuickPreview);
        Assert.Contains("15:00", main.QuickPreview);
        Assert.Contains("打合せ", main.QuickPreview);
        Assert.Contains("会議室A", main.QuickPreview);
    }

    [Fact]
    public void 空なら何も出さない()
    {
        using var test = TestWorkspace.Create();
        var main = Create(test);

        Assert.Null(main.QuickPreview);
        Assert.False(main.QuickCommand.CanExecute(null));
    }

    [Fact]
    public void 日付を書かなければ今日に入る()
    {
        using var test = TestWorkspace.Create();
        var main = Create(test);

        // 別の日を眺めていても、基準は今日
        main.SelectedDate = new DateOnly(2026, 9, 30);

        main.QuickText = "棚卸";
        main.QuickCommand.Execute(null);

        Assert.Equal(Today, test.Workspace.Events.All().Single(e => e.Title == "棚卸").Date);
    }

    [Fact]
    public void 明日は今日から見た明日()
    {
        using var test = TestWorkspace.Create();
        var main = Create(test);

        // 来月を眺めている最中でも、「明日」は今日の次の日
        main.SelectedDate = new DateOnly(2026, 10, 15);

        main.QuickText = "明日 打合せ";
        main.QuickCommand.Execute(null);

        Assert.Equal(Today.AddDays(1),
            test.Workspace.Events.All().Single(e => e.Title == "打合せ").Date);
    }
}
