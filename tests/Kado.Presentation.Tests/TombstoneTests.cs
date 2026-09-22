using Kado.Data.Models;
using Kado.Data.Repositories;

namespace Kado.Presentation.Tests;

/// <summary>
/// 消したことの記録。
/// <para>
/// 残さないと、次の同期で消したものが復活する。こちらで消しただけでは相手には
/// まだ残っていて、「こちらに無い予定」として降ってくるため。
/// </para>
/// </summary>
public class TombstoneTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void 同期済みの予定を消すと記録が残る()
    {
        using var test = TestWorkspace.Create();

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24),
            GoogleEventId = "g1", Source = "google",
        });

        test.Workspace.DeleteEvent("e1");

        var pending = Assert.Single(test.Workspace.Tombstones.Pending(TombstoneRepository.EventKind));
        Assert.Equal("e1", pending.Id);
        Assert.Equal("g1", pending.GoogleId);
    }

    [Fact]
    public void 記録には入れ先のカレンダーも残る()
    {
        using var test = TestWorkspace.Create();

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24),
            CalendarId = "shigoto", GoogleEventId = "g1", Source = "google",
        });

        test.Workspace.DeleteEvent("e1");

        // 持ち主が分からないと、同期のときに別のカレンダーへ削除を投げて 404 になり、
        // 本当の持ち主には一度も届かない
        var pending = Assert.Single(test.Workspace.Tombstones.Pending(TombstoneRepository.EventKind));
        Assert.Equal("shigoto", pending.SourceId);

        // 持ち主で絞れる
        Assert.Single(test.Workspace.Tombstones.Pending(TombstoneRepository.EventKind, "shigoto"));
        Assert.Empty(test.Workspace.Tombstones.Pending(TombstoneRepository.EventKind, "shumi"));
    }

    [Fact]
    public void 持ち主の分からない記録はどのカレンダーでも拾う()
    {
        using var test = TestWorkspace.Create();

        // 版を上げる前から残っている記録のつもり
        test.Workspace.Tombstones.Record(
            "e1", TombstoneRepository.EventKind, "g1", DateTimeOffset.Now);

        Assert.Single(test.Workspace.Tombstones.Pending(TombstoneRepository.EventKind, "shigoto"));
        Assert.Single(test.Workspace.Tombstones.Pending(TombstoneRepository.EventKind, "shumi"));
    }

    [Fact]
    public void 同期していない予定では記録を残さない()
    {
        using var test = TestWorkspace.Create();

        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "手元だけ", Date = D(2026, 9, 24) });
        test.Workspace.DeleteEvent("e1");

        // 相手に無いものは伝えようがない
        Assert.Equal(0, test.Workspace.Tombstones.Count());
    }

    [Fact]
    public void 元に戻すと記録も消える()
    {
        using var test = TestWorkspace.Create();

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24), GoogleEventId = "g1",
        });
        test.Workspace.DeleteEvent("e1");
        test.Workspace.UndoLast();

        // 消していないことになったのだから、相手へ伝えては困る
        Assert.Equal(0, test.Workspace.Tombstones.Count());
        Assert.NotNull(test.Workspace.Events.Find("e1"));
    }

    [Fact]
    public void やり直すと記録が戻る()
    {
        using var test = TestWorkspace.Create();

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24), GoogleEventId = "g1",
        });
        test.Workspace.DeleteEvent("e1");
        test.Workspace.UndoLast();
        test.Workspace.RedoLast();

        Assert.Single(test.Workspace.Tombstones.Pending(TombstoneRepository.EventKind));
    }

    [Fact]
    public void 同期済みのタスクを消すと記録が残る()
    {
        using var test = TestWorkspace.Create();

        test.Workspace.AddTask(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24),
            GoogleTaskId = "gt1", GoogleTaskListId = "@default",
        });

        test.Workspace.DeleteTask("t1");

        var pending = Assert.Single(test.Workspace.Tombstones.Pending(TombstoneRepository.TaskKind));
        Assert.Equal("gt1", pending.GoogleId);
    }

    [Fact]
    public void タスクを元に戻すと記録も作業時間も戻る()
    {
        using var test = TestWorkspace.Create();

        test.Workspace.AddTask(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), GoogleTaskId = "gt1",
        });
        test.Workspace.DeleteTask("t1");
        test.Workspace.UndoLast();

        Assert.Equal(0, test.Workspace.Tombstones.Count());
        Assert.NotNull(test.Workspace.Tasks.Find("t1"));
    }

    [Fact]
    public void 予定とタスクの記録は混ざらない()
    {
        using var test = TestWorkspace.Create();

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "x1", Title = "定例", Date = D(2026, 9, 24), GoogleEventId = "g1",
        });
        test.Workspace.AddTask(new TaskItem
        {
            Id = "x1", Title = "集計", Due = D(2026, 9, 24), GoogleTaskId = "gt1",
        });

        test.Workspace.DeleteEvent("x1");
        test.Workspace.DeleteTask("x1");

        // 同じ識別子でも別経路。種別で分けて持つ
        Assert.Single(test.Workspace.Tombstones.Pending(TombstoneRepository.EventKind));
        Assert.Single(test.Workspace.Tombstones.Pending(TombstoneRepository.TaskKind));
    }
}
