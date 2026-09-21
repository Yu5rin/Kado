using SlideinaCalendar.Data.Models;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 編集はすべてワークスペースを通し、Undo に積まれること。
/// </summary>
public class CalendarWorkspaceTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static CalendarEvent Event(string id, DateOnly? date = null) => new()
    {
        Id = id,
        Title = $"予定{id}",
        Date = date ?? D(2026, 9, 24),
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    // ------------------------------------------------------------------
    // 予定
    // ------------------------------------------------------------------

    [Fact]
    public void 予定の追加を元に戻せる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Event("e1"));
        Assert.Equal(1, ws.Events.Count());

        ws.UndoLast();
        Assert.Equal(0, ws.Events.Count());

        ws.RedoLast();
        Assert.Equal(1, ws.Events.Count());
    }

    [Fact]
    public void 予定の変更を元に戻せる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Event("e1"));
        ws.UpdateEvent(Event("e1") with { Title = "変更後" });

        Assert.Equal("変更後", ws.Events.Find("e1")!.Title);

        ws.UndoLast();
        Assert.Equal("予定e1", ws.Events.Find("e1")!.Title);
    }

    [Fact]
    public void 予定の削除を元に戻せる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Event("e1") with { Note = "消えては困るメモ" });
        ws.DeleteEvent("e1");

        Assert.Null(ws.Events.Find("e1"));

        ws.UndoLast();
        // 中身ごと戻ること。識別子だけ作り直しても意味がない
        Assert.Equal("消えては困るメモ", ws.Events.Find("e1")!.Note);
    }

    [Fact]
    public void いない予定の変更と削除は何も起きない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        Assert.False(ws.UpdateEvent(Event("いない")));
        Assert.False(ws.DeleteEvent("いない"));
        Assert.False(ws.Undo.CanUndo);
    }

    // ------------------------------------------------------------------
    // タスク
    // ------------------------------------------------------------------

    [Fact]
    public void タスクの完了を切り替えて元に戻せる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(new TaskItem { Id = "t1", Title = "やること" });

        ws.ToggleTaskDone("t1");
        Assert.True(ws.Tasks.Find("t1")!.IsDone);

        ws.UndoLast();
        Assert.False(ws.Tasks.Find("t1")!.IsDone);
    }

    [Fact]
    public void 完了にすると完了日時が入る()
    {
        // 完了日時が無いと「N実働日 遅れて完了」が出ない（項目3）
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(new TaskItem { Id = "t1", Title = "やること" });
        ws.ToggleTaskDone("t1");

        Assert.NotNull(ws.Tasks.Find("t1")!.CompletedAt);
    }

    [Fact]
    public void 完了を取り消すと完了日時も消える()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(new TaskItem { Id = "t1", Title = "やること" });
        ws.ToggleTaskDone("t1");
        ws.ToggleTaskDone("t1");

        Assert.Null(ws.Tasks.Find("t1")!.CompletedAt);
    }

    [Fact]
    public void 完了日時を元に戻すとまた消える()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(new TaskItem { Id = "t1", Title = "やること" });
        ws.ToggleTaskDone("t1");
        Assert.NotNull(ws.Tasks.Find("t1")!.CompletedAt);

        ws.UndoLast();
        Assert.Null(ws.Tasks.Find("t1")!.CompletedAt);
    }

    [Fact]
    public void 完了の切り替えは説明が分かれる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(new TaskItem { Id = "t1", Title = "やること" });

        ws.ToggleTaskDone("t1");
        // 「タスクの変更」では何が戻るのか分からない
        Assert.Equal("タスクを完了にする", ws.Undo.UndoDescription);

        ws.ToggleTaskDone("t1");
        Assert.Equal("タスクの完了を取り消す", ws.Undo.UndoDescription);
    }

    [Fact]
    public void タスクの変更は完了切り替えと区別される()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(new TaskItem { Id = "t1", Title = "やること" });
        ws.UpdateTask(new TaskItem { Id = "t1", Title = "書き換えた" });

        Assert.Equal("タスクの変更", ws.Undo.UndoDescription);
    }

    [Fact]
    public void タスクを消すと作業時間ブロックも戻る()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(new TaskItem { Id = "t1", Title = "作業のあるタスク" });
        ws.Tasks.UpsertBlock(new WorkBlock
        {
            Id = "b1", TaskId = "t1", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0), DurationMinutes = 90,
        });

        ws.DeleteTask("t1");
        Assert.Empty(ws.Tasks.BlocksOf("t1"));

        ws.UndoLast();

        // 連鎖で消えたぶんを控えていないと、ここで失われる
        var block = Assert.Single(ws.Tasks.BlocksOf("t1"));
        Assert.Equal(90, block.DurationMinutes);
    }

    // ------------------------------------------------------------------
    // 通知と履歴
    // ------------------------------------------------------------------

    [Fact]
    public void 編集のたびに変更が通知される()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        var notified = 0;
        ws.DataChanged += (_, _) => notified++;

        ws.AddEvent(Event("e1"));
        ws.UndoLast();
        ws.RedoLast();

        Assert.Equal(3, notified);
    }

    [Fact]
    public void 戻すものが無ければ通知されない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        var notified = 0;
        ws.DataChanged += (_, _) => notified++;

        Assert.Null(ws.UndoLast());
        Assert.Null(ws.RedoLast());
        Assert.Equal(0, notified);
    }

    [Fact]
    public void 実働日カレンダーが読み込まれている()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        Assert.Equal(19, ws.WorkingDays.CountInMonth(2026, 9));
        Assert.True(ws.WorkingDays.IsWorkingDay(D(2026, 9, 24)));
        Assert.False(ws.WorkingDays.IsWorkingDay(D(2026, 9, 21)));

        // 期限の表示も組み立てられる
        Assert.Equal("残り 1実働日", ws.DueFormatter.Format(D(2026, 9, 25), D(2026, 9, 24)).Text);
    }
}
