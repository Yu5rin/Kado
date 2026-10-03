using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Mapping;

namespace Kado.Presentation.Tests;

/// <summary>
/// Google と同期したあとの「元に戻す」「やり直し」。
/// <para>
/// 戻すために書き込む姿は、同期の前に控えた古いものそのまま、ではいけない。
/// 同期は Google との結び付き（ID・入れ先・控えた生データ）を書き換えているので、
/// 古い結び付きで上書きすると、戻した内容が送られなかったり、Google 側に二重にできたり、
/// 消した予定が手元にだけ復活したりする。
/// <b>中身は編集の前後から、結び付きは書き込む時点の最新の行から</b>採る。
/// </para>
/// </summary>
public class SyncedUndoTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static CalendarEvent Local(string id, string calendar = "cal-a") => new()
    {
        Id = id, Title = $"予定{id}", Date = D(2026, 9, 24), CalendarId = calendar,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>Google が返してくる姿のつもり。こちらの中身を当てたものに id を足す。</summary>
    private static string Raw(CalendarEvent value, string googleId = "g1")
    {
        var body = EventMapper.ToGoogle(value);
        body["id"] = googleId;
        return body.ToJsonString();
    }

    /// <summary>同期が済んだ姿。Google の ID・入れ先・控えた生データが入る。</summary>
    private static CalendarEvent Synced(
        CalendarEvent value, string googleCalendar, string googleId = "g1", string updated = "2026-09-19T00:00:00.000Z") =>
        value with
        {
            GoogleEventId = googleId, GoogleCalendarId = googleCalendar, GoogleRaw = Raw(value, googleId),
            GoogleUpdated = updated, Source = "google", Status = "confirmed",
        };

    // ------------------------------------------------------------------
    // 追加の取り消し・やり直し
    // ------------------------------------------------------------------

    [Fact]
    public void 同期した予定の追加を元に戻すとGoogleからも消す()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Local("e1"));
        ws.Events.Upsert(Synced(ws.Events.Find("e1")!, "cal-a"));

        ws.UndoLast();

        Assert.Null(ws.Events.Find("e1"));

        // 手元の行を消すだけでは Google に残り、次の同期で降ってきて復活する
        var pending = Assert.Single(ws.Tombstones.Pending(TombstoneRepository.EventKind));
        Assert.Equal("g1", pending.GoogleId);
        Assert.Equal("cal-a", pending.SourceId);
    }

    [Fact]
    public void 同期していない予定の追加を元に戻しても記録は残さない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Local("e1"));
        ws.UndoLast();

        Assert.Equal(0, ws.Tombstones.Count());
    }

    [Fact]
    public void 同期した予定の追加を元に戻してやり直すとGoogleのIDを持たない新規として入る()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Local("e1"));
        ws.Events.Upsert(Synced(ws.Events.Find("e1")!, "cal-a"));
        ws.UndoLast();

        ws.RedoLast();

        // 古い ID のまま戻すと、消した Google 側と結び付き直して食い違う。
        // 新しい予定として入れれば、新規として送られ、同じものが2つにならない
        var again = Assert.Single(ws.Events.All());
        Assert.Equal("e1", again.Id);
        Assert.Null(again.GoogleEventId);
        Assert.Null(again.GoogleRaw);
        Assert.Null(again.GoogleCalendarId);

        // 前の Google 側の分を消す記録は、伝え終わるまで残る
        Assert.Single(ws.Tombstones.Pending(TombstoneRepository.EventKind));
    }

    // ------------------------------------------------------------------
    // 変更の取り消し・やり直し
    // ------------------------------------------------------------------

    [Fact]
    public void 同期のあとで変更を元に戻すと戻した内容が送られる状態になる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Synced(Local("e1"), "cal-a"));

        // 変更して、同期で Google に送り終えた
        ws.UpdateEvent(ws.Events.Find("e1")! with { Title = "変更後" });
        var sent = ws.Events.Find("e1")!;
        var sentRaw = Raw(sent);
        ws.Events.Upsert(sent with { GoogleRaw = sentRaw, GoogleUpdated = "2026-09-19T01:00:00.000Z" });

        ws.UndoLast();

        var reverted = ws.Events.Find("e1")!;
        Assert.Equal("予定e1", reverted.Title);

        // Google に控えてあるのは「変更後」のまま。戻した内容は送られなければならない。
        // 古い控え（変更前の姿）で上書きすると、見かけ上「同期済み」になって送られない
        Assert.True(EventMapper.NeedsPush(reverted));
        Assert.Equal(sentRaw, reverted.GoogleRaw);
        Assert.Equal("2026-09-19T01:00:00.000Z", reverted.GoogleUpdated);
    }

    [Fact]
    public void 同期のあとで元に戻した変更をやり直すと送られる状態になる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Synced(Local("e1"), "cal-a"));
        ws.UpdateEvent(ws.Events.Find("e1")! with { Title = "変更後" });
        ws.UndoLast();

        // 戻した内容も同期で送り終えた
        var sent = ws.Events.Find("e1")!;
        ws.Events.Upsert(sent with { GoogleRaw = Raw(sent), GoogleUpdated = "2026-09-19T02:00:00.000Z" });

        ws.RedoLast();

        var redone = ws.Events.Find("e1")!;
        Assert.Equal("変更後", redone.Title);
        Assert.True(EventMapper.NeedsPush(redone));
        Assert.Equal("2026-09-19T02:00:00.000Z", redone.GoogleUpdated);
    }

    [Fact]
    public void 移動を元に戻してもGoogleでの入れ先は保つ()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Synced(Local("e1", "cal-a"), "cal-a"));

        // 入れ先を A から B へ。同期が events.move で運び終えた
        ws.UpdateEvent(ws.Events.Find("e1")! with { CalendarId = "cal-b" });
        ws.Events.Upsert(ws.Events.Find("e1")! with { GoogleCalendarId = "cal-b" });

        ws.UndoLast();

        var reverted = ws.Events.Find("e1")!;

        // 希望は A に戻るが、Google での実際の場所は B のまま。
        // 実際の場所まで A に戻すと「A から A へ」で移す必要が無いことになり、
        // その後の編集が A への patch になって 404 → 作り直しで二重になる
        Assert.Equal("cal-a", reverted.CalendarId);
        Assert.Equal("cal-b", reverted.GoogleCalendarId);
    }

    [Fact]
    public void 移動のやり直しも最新の入れ先を保つ()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Synced(Local("e1", "cal-a"), "cal-a"));
        ws.UpdateEvent(ws.Events.Find("e1")! with { CalendarId = "cal-b" });
        ws.Events.Upsert(ws.Events.Find("e1")! with { GoogleCalendarId = "cal-b" });
        ws.UndoLast();

        // 戻す移動も済んだ（B → A）
        ws.Events.Upsert(ws.Events.Find("e1")! with { GoogleCalendarId = "cal-a" });

        ws.RedoLast();

        var redone = ws.Events.Find("e1")!;
        Assert.Equal("cal-b", redone.CalendarId);
        Assert.Equal("cal-a", redone.GoogleCalendarId);
    }

    [Fact]
    public void 開いている間に消えた予定の変更は元に戻しても復活させない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Synced(Local("e1"), "cal-a"));
        ws.UpdateEvent(ws.Events.Find("e1")! with { Title = "変更後" });

        // Google 側で消されて、同期が手元からも消した
        ws.Events.Delete("e1");

        ws.UndoLast();

        Assert.Null(ws.Events.Find("e1"));
        Assert.Equal(0, ws.Tombstones.Count());
    }

    // ------------------------------------------------------------------
    // 削除の取り消し・やり直し
    // ------------------------------------------------------------------

    [Fact]
    public void まだ伝えていない削除を元に戻すと元通りになる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Synced(Local("e1"), "cal-a"));
        ws.DeleteEvent("e1");

        ws.UndoLast();

        var restored = ws.Events.Find("e1")!;
        Assert.Equal("g1", restored.GoogleEventId);
        Assert.Equal("cal-a", restored.GoogleCalendarId);
        Assert.NotNull(restored.GoogleRaw);
        Assert.Equal(0, ws.Tombstones.Count());
    }

    [Fact]
    public void もう伝え終えた削除を元に戻すとGoogleのIDを持たない新規として作り直す()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Synced(Local("e1"), "cal-a"));
        ws.DeleteEvent("e1");

        // 同期が削除を伝え終えて、記録を片付けた。Google からは消えている
        ws.Tombstones.Clear("e1", TombstoneRepository.EventKind);

        ws.UndoLast();

        // 古い ID のまま手元にだけ戻すと、Google には無いのに「ある」ことになり、
        // 次の編集の patch が 404 になる
        var restored = ws.Events.Find("e1")!;
        Assert.Equal("予定e1", restored.Title);
        Assert.Null(restored.GoogleEventId);
        Assert.Null(restored.GoogleRaw);
        Assert.Null(restored.GoogleCalendarId);
        Assert.Equal(0, ws.Tombstones.Count());

        // 新規として送られる対象になっている
        Assert.True(EventMapper.NeedsPush(restored));
    }

    [Fact]
    public void 新規として作り直した予定を削除し直しても_Googleへは伝えない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(Synced(Local("e1"), "cal-a"));
        ws.DeleteEvent("e1");
        ws.Tombstones.Clear("e1", TombstoneRepository.EventKind);
        ws.UndoLast();

        ws.RedoLast();

        Assert.Null(ws.Events.Find("e1"));

        // Google にはもう無い。古い ID の削除をまた投げない
        Assert.Equal(0, ws.Tombstones.Count());
    }

    // ------------------------------------------------------------------
    // 送る前に削除したとき、持ち主は Google で実際にいる場所
    // ------------------------------------------------------------------

    [Fact]
    public void 移す指示を出したあと送る前に削除したら_Googleでいる側のカレンダーを持ち主にする()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        // 入れ先を A から B へ変えたが、まだ events.move は送っていない
        ws.AddEvent(Synced(Local("e1", "cal-b"), "cal-a"));

        ws.DeleteEvent("e1");

        // 持ち主を B にすると、同期は B へ削除を投げて 404 になり、「すでに無い」と
        // 受け取って記録を捨てる。本当にいる A には届かず、予定が Google に残る
        var pending = Assert.Single(ws.Tombstones.Pending(TombstoneRepository.EventKind));
        Assert.Equal("cal-a", pending.SourceId);
    }

    // ------------------------------------------------------------------
    // タスク。考え方は予定と同じ
    // ------------------------------------------------------------------

    private static TaskItem LocalTask(string id, string list = "list-a") => new()
    {
        Id = id, Title = $"タスク{id}", Due = D(2026, 9, 24), TaskListId = list,
        UpdatedAt = DateTimeOffset.UnixEpoch, CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static string TaskRaw(TaskItem value, string googleId = "t1")
    {
        var body = TaskMapper.ToGoogle(value);
        body["id"] = googleId;
        return body.ToJsonString();
    }

    private static TaskItem SyncedTask(TaskItem value, string googleList, string googleId = "t1") =>
        value with
        {
            GoogleTaskId = googleId, GoogleTaskListId = googleList, GoogleRaw = TaskRaw(value, googleId),
            GoogleUpdated = "2026-09-19T00:00:00.000Z", Source = "google",
            ParentId = "parent-1", Position = "00000000000000000001",
        };

    [Fact]
    public void 同期したタスクの追加を元に戻すとGoogleからも消す()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(LocalTask("t1"));
        ws.Tasks.Upsert(SyncedTask(ws.Tasks.Find("t1")!, "list-a"));

        ws.UndoLast();

        Assert.Null(ws.Tasks.Find("t1"));
        var pending = Assert.Single(ws.Tombstones.Pending(TombstoneRepository.TaskKind));
        Assert.Equal("t1", pending.GoogleId);
        Assert.Equal("list-a", pending.SourceId);
    }

    [Fact]
    public void 同期した追加を元に戻してやり直したタスクはGoogleのIDを持たない新規になる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(LocalTask("t1"));
        ws.Tasks.Upsert(SyncedTask(ws.Tasks.Find("t1")!, "list-a"));
        ws.UndoLast();

        ws.RedoLast();

        var again = Assert.Single(ws.Tasks.All());
        Assert.Null(again.GoogleTaskId);
        Assert.Null(again.GoogleRaw);
        Assert.Null(again.ParentId);
        Assert.Single(ws.Tombstones.Pending(TombstoneRepository.TaskKind));
    }

    [Fact]
    public void 同期のあとでタスクの変更を元に戻すと戻した内容が送られる状態になる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(SyncedTask(LocalTask("t1"), "list-a"));
        ws.UpdateTask(ws.Tasks.Find("t1")! with { Title = "変更後" });
        var sent = ws.Tasks.Find("t1")!;
        ws.Tasks.Upsert(sent with { GoogleRaw = TaskRaw(sent), GoogleUpdated = "2026-09-19T01:00:00.000Z" });

        ws.UndoLast();

        var reverted = ws.Tasks.Find("t1")!;
        Assert.Equal("タスクt1", reverted.Title);
        Assert.True(TaskMapper.NeedsPush(reverted));

        // 親子関係と並び順は Google が持つ。戻す側が古い値で書き戻さない
        Assert.Equal("parent-1", reverted.ParentId);
        Assert.Equal("00000000000000000001", reverted.Position);
    }

    [Fact]
    public void タスクの移動を元に戻してもGoogleでの入れ先は保つ()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(SyncedTask(LocalTask("t1", "list-a"), "list-a"));
        ws.UpdateTask(ws.Tasks.Find("t1")! with { TaskListId = "list-b" });
        ws.Tasks.Upsert(ws.Tasks.Find("t1")! with { GoogleTaskListId = "list-b" });

        ws.UndoLast();

        var reverted = ws.Tasks.Find("t1")!;
        Assert.Equal("list-a", reverted.TaskListId);
        Assert.Equal("list-b", reverted.GoogleTaskListId);
    }

    [Fact]
    public void もう伝え終えたタスクの削除を元に戻すとGoogleのIDを持たない新規として作り直す()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(SyncedTask(LocalTask("t1"), "list-a"));
        ws.DeleteTask("t1");
        ws.Tombstones.Clear("t1", TombstoneRepository.TaskKind);

        ws.UndoLast();

        var restored = ws.Tasks.Find("t1")!;
        Assert.Equal("タスクt1", restored.Title);
        Assert.Null(restored.GoogleTaskId);
        Assert.Null(restored.GoogleRaw);
        Assert.Equal(0, ws.Tombstones.Count());
    }

    [Fact]
    public void まだ伝えていないタスクの削除を元に戻すと元通りになる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(SyncedTask(LocalTask("t1"), "list-a"));
        ws.DeleteTask("t1");

        ws.UndoLast();

        Assert.Equal("t1", ws.Tasks.Find("t1")!.GoogleTaskId);
        Assert.Equal(0, ws.Tombstones.Count());
    }
}
