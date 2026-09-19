using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// ドラッグで別の日へ移す。Ctrl を押しながらなら複製。
/// <para>受け口は表示側にあるが、動かすのは ViewModel なのでここで確かめる。</para>
/// </summary>
public class DragMoveTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);
    private static readonly DateOnly Tomorrow = new(2026, 9, 25);

    private static MainViewModel Create(TestWorkspace test) => new(test.Workspace, Today);

    private static CalendarEvent Event(string id, DateOnly date, DateOnly? end = null) => new()
    {
        Id = id, Title = "打ち合わせ", Date = date, EndDate = end,
        StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
    };

    [Fact]
    public void 予定を別の日へ移せる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        var main = Create(test);
        Assert.True(main.MoveEventTo("e1", Tomorrow));

        Assert.Equal(Tomorrow, test.Workspace.Events.Find("e1")!.Date);
    }

    [Fact]
    public void 移しても時刻は変わらない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        Create(test).MoveEventTo("e1", Tomorrow);

        var moved = test.Workspace.Events.Find("e1")!;
        Assert.Equal(new TimeOnly(10, 0), moved.StartTime);
        Assert.Equal(new TimeOnly(11, 0), moved.EndTime);
    }

    [Fact]
    public void 期間のある予定は長さを保つ()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today, Today.AddDays(2)));

        Create(test).MoveEventTo("e1", Tomorrow);

        var moved = test.Workspace.Events.Find("e1")!;
        Assert.Equal(Tomorrow, moved.Date);
        Assert.Equal(Tomorrow.AddDays(2), moved.EndDate);
    }

    [Fact]
    public void 複製すると元が残る()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        Create(test).MoveEventTo("e1", Tomorrow, copy: true);

        Assert.Equal(Today, test.Workspace.Events.Find("e1")!.Date);
        Assert.Equal(2, test.Workspace.Events.All().Count(e => e.Title == "打ち合わせ"));
        Assert.Contains(test.Workspace.Events.All(), e => e.Date == Tomorrow && e.Id != "e1");
    }

    [Fact]
    public void 複製は相手側の識別子を引き継がない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today) with
        {
            GoogleEventId = "g1", GoogleRaw = "{}", Source = "google",
        });

        Create(test).MoveEventTo("e1", Tomorrow, copy: true);

        // 引き継ぐと、次の同期で元の予定のほうが書き換わる
        var copy = test.Workspace.Events.All().Single(e => e.Id != "e1");
        Assert.Null(copy.GoogleEventId);
        Assert.Null(copy.GoogleRaw);
    }

    [Fact]
    public void 実働日データの印は動かせない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "closedday:2026-09-24", Title = CalendarWorkspace.ClosedDayTitle,
            Date = Today, Source = CalendarWorkspace.WorkingDaySource,
        });

        Assert.False(Create(test).MoveEventTo("closedday:2026-09-24", Tomorrow));
        Assert.Equal(Today, test.Workspace.Events.Find("closedday:2026-09-24")!.Date);
    }

    [Fact]
    public void 同じ日に落としても何も起きない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        Assert.False(Create(test).MoveEventTo("e1", Today));
    }

    [Fact]
    public void 移したあとは元に戻せる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        var main = Create(test);
        main.MoveEventTo("e1", Tomorrow);
        main.UndoCommand.Execute(null);

        Assert.Equal(Today, test.Workspace.Events.Find("e1")!.Date);
    }

    [Fact]
    public void タスクの期限も移せる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "資料作成", Due = Today });

        Assert.True(Create(test).MoveTaskTo("t1", Tomorrow));
        Assert.Equal(Tomorrow, test.Workspace.Tasks.Find("t1")!.Due);
    }

    [Fact]
    public void タスクも複製できる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "資料作成", Due = Today });

        Create(test).MoveTaskTo("t1", Tomorrow, copy: true);

        Assert.Equal(Today, test.Workspace.Tasks.Find("t1")!.Due);
        Assert.Contains(test.Workspace.Tasks.All(), t => t.Due == Tomorrow && t.Id != "t1");
    }

    [Fact]
    public void 無い予定を移そうとしても落ちない()
    {
        using var test = TestWorkspace.Create();
        var main = Create(test);

        Assert.False(main.MoveEventTo("ない", Tomorrow));
        Assert.False(main.MoveEventTo(null, Tomorrow));
        Assert.False(main.MoveTaskTo("ない", Tomorrow));
    }
}
