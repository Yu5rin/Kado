using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

public class WeekViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static TimeOnly T(int h, int m = 0) => new(h, m);

    private static WeekViewModel Create(TestWorkspace test, DateOnly? anchor = null) =>
        new(test.Workspace, anchor ?? D(2026, 9, 24), today: D(2026, 9, 24));

    [Fact]
    public void 週は開始曜日から7日並ぶ()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 9/24 は木曜。日曜始まりなので 9/20〜9/26
        Assert.Equal(D(2026, 9, 20), vm.WeekStart);
        Assert.Equal(D(2026, 9, 26), vm.WeekEnd);
        Assert.Equal(7, vm.Days.Count);
        Assert.Equal("2026年9月20日 〜 26日", vm.Title);
    }

    [Fact]
    public void 月曜始まりにもできる()
    {
        using var test = TestWorkspace.Create();
        var vm = new WeekViewModel(test.Workspace, D(2026, 9, 24), D(2026, 9, 24), DayOfWeek.Monday);

        Assert.Equal(D(2026, 9, 21), vm.WeekStart);
        Assert.Equal(D(2026, 9, 27), vm.WeekEnd);
    }

    [Fact]
    public void 月をまたぐ週は両方に月を付ける()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 30));

        Assert.Equal("2026年9月27日 〜 10月3日", vm.Title);
    }

    [Fact]
    public void 日付ヘッダには実働日の通し番号を出す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 月ビューのマスには出さないが、週ビューの日付ヘッダは出す場所（要件書 4.3）
        Assert.Equal("実働 15日目", vm.Days.Single(d => d.Date == D(2026, 9, 24)).WorkingDayLabel);

        // 非稼働日には出ない
        Assert.Null(vm.Days.Single(d => d.Date == D(2026, 9, 21)).WorkingDayLabel);
    }

    [Fact]
    public void 非稼働日は面を沈める()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.True(vm.Days.Single(d => d.Date == D(2026, 9, 21)).IsDimmed);   // 敬老の日
        Assert.False(vm.Days.Single(d => d.Date == D(2026, 9, 24)).IsDimmed);
    }

    [Fact]
    public void 祝日名は曜日に続けて出す()
    {
        using var test = TestWorkspace.Create(
            holidays: new Dictionary<DateOnly, string> { [D(2026, 9, 21)] = "敬老の日" });

        var vm = Create(test);
        var day = vm.Days.Single(d => d.Date == D(2026, 9, 21));

        Assert.Equal("月 敬老の日", day.HeaderText);
        Assert.Equal("木", vm.Days.Single(d => d.Date == D(2026, 9, 24)).HeaderText);
    }

    [Fact]
    public void 終日は上のレーン_時刻付きは時間軸に置く()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "月次棚卸", Date = D(2026, 9, 24),
        });
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e2", Title = "計画レビュー", Date = D(2026, 9, 24),
            StartTime = T(9), EndTime = T(10, 30),
        });

        var day = Create(test).Days.Single(d => d.Date == D(2026, 9, 24));

        Assert.Equal("月次棚卸", Assert.Single(day.AllDayEvents).Label);
        var block = Assert.Single(day.Blocks);
        Assert.Equal("計画レビュー", block.Title);
        Assert.Equal("09:00", block.TimeText);
    }

    [Fact]
    public void 置き場所はモックと同じ1時間44pxで決まる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "計画レビュー", Date = D(2026, 9, 24),
            StartTime = T(9), EndTime = T(10, 30),
        });

        var block = Assert.Single(Create(test).Days.Single(d => d.Date == D(2026, 9, 24)).Blocks);

        // 表示開始は 8 時。9 時は 1 時間ぶん下
        Assert.Equal(44, block.Top);
        // 1時間30分ぶんから、罫線に乗らないよう 3 詰める
        Assert.Equal(44 * 1.5 - 3, block.Height);
    }

    [Fact]
    public void 表示時間帯からはみ出す分は端で切る()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "早出", Date = D(2026, 9, 24), StartTime = T(6), EndTime = T(9),
        });

        var block = Assert.Single(Create(test).Days.Single(d => d.Date == D(2026, 9, 24)).Blocks);

        Assert.Equal(0, block.Top);          // 8 時より前は上端で止める
        Assert.Equal(44 - 3, block.Height);  // 8〜9 時の1時間ぶんだけ残る
    }

    [Fact]
    public void 表示時間帯から完全に外れた予定は置かない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "深夜作業", Date = D(2026, 9, 24), StartTime = T(2), EndTime = T(4),
        });

        Assert.Empty(Create(test).Days.Single(d => d.Date == D(2026, 9, 24)).Blocks);
    }

    [Fact]
    public void 終了時刻の無い予定にも高さを与える()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "朝礼", Date = D(2026, 9, 24), StartTime = T(9),
        });

        var block = Assert.Single(Create(test).Days.Single(d => d.Date == D(2026, 9, 24)).Blocks);

        // 潰れて読めなくならないよう 30 分の枠を置く
        Assert.Equal(T(9, 30), block.End);
        Assert.True(block.Height > 0);
    }

    [Fact]
    public void 作業時間ブロックは予定と区別できる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "治具の発注手配", Due = D(2026, 9, 24) });
        test.Workspace.Tasks.UpsertBlock(new WorkBlock
        {
            Id = "b1", TaskId = "t1", Date = D(2026, 9, 24), StartTime = T(15, 30), DurationMinutes = 90,
        });

        var day = Create(test).Days.Single(d => d.Date == D(2026, 9, 24));
        var block = Assert.Single(day.Blocks);

        // 予定には変換しない（要件書 5.4）。表示でも点線枠で分ける
        Assert.True(block.IsWorkBlock);
        Assert.Equal("治具の発注手配", block.Title);

        // 期限付きタスクは終日レーンにも並ぶ。ここから時間帯へドラッグする
        Assert.Single(day.Tasks);
    }

    [Fact]
    public void 時間軸の見出しは表示時間帯のぶんだけ出る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.Equal(["8:00", "9:00", "10:00", "11:00", "12:00", "13:00",
                      "14:00", "15:00", "16:00", "17:00", "18:00", "19:00"], vm.HourLabels);
        Assert.Equal(12 * 44, vm.TimelineHeight);
    }

    [Fact]
    public void 現在時刻の線は今日を含む週にだけ出す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.UpdateNowLine(T(10));
        Assert.True(vm.ShowNowLine);
        Assert.Equal(88, vm.NowOffset);   // 8 時から 2 時間ぶん

        // 表示時間帯の外
        vm.UpdateNowLine(T(6));
        Assert.False(vm.ShowNowLine);

        // 別の週
        vm.GoToNextWeek();
        vm.UpdateNowLine(T(10));
        Assert.False(vm.ShowNowLine);
    }

    [Fact]
    public void 週を送れる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.GoToNextWeek();
        Assert.Equal(D(2026, 9, 27), vm.WeekStart);

        vm.GoToPreviousWeek();
        vm.GoToPreviousWeek();
        Assert.Equal(D(2026, 9, 13), vm.WeekStart);

        vm.GoToToday();
        Assert.Equal(D(2026, 9, 20), vm.WeekStart);
    }

    [Fact]
    public void 表示していないカレンダーは時間軸にも出ない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "会議", Date = D(2026, 9, 24),
            StartTime = T(9), EndTime = T(10), CalendarId = "私用",
        });

        var lists = new SourceListsViewModel(test.Workspace);
        lists.Calendars.Single(c => c.Name == "私用").IsVisible = false;

        var vm = new WeekViewModel(test.Workspace, D(2026, 9, 24), D(2026, 9, 24), sources: lists);

        Assert.Empty(vm.Days.Single(d => d.Date == D(2026, 9, 24)).Blocks);
    }
}
