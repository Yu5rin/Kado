using Kado.Data.Models;
using Kado.Google.Mapping;
using Kado.Presentation.Editing;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 編集画面を開いている間に同期が走ったとき。
/// <para>
/// 裏の同期はモーダルの間も走る。開いた時点の写しで保存すると、その間に同期が進めた
/// Google との結び付き（入れ先・控えた生データ・更新時刻）を古い値で書き戻し、
/// 移し終えたものを元に戻して二重にしたり、Google 側の変更を警告なしで戻したりする。
/// </para>
/// </summary>
public class EditDuringSyncTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static (MainViewModel Vm, FakeEditorPresenter Editors) Create(TestWorkspace test)
    {
        var editors = new FakeEditorPresenter();
        return (new MainViewModel(test.Workspace, today: D(2026, 9, 24), editors: editors), editors);
    }

    private static string Raw(CalendarEvent value)
    {
        var body = EventMapper.ToGoogle(value);
        body["id"] = "g1";
        return body.ToJsonString();
    }

    /// <summary>Google と同期済みの予定。</summary>
    private static CalendarEvent Synced(string calendar = "cal-a", string googleCalendar = "cal-a")
    {
        var value = new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24), CalendarId = calendar,
            GoogleEventId = "g1", GoogleCalendarId = googleCalendar, Source = "google",
            GoogleUpdated = "2026-09-19T00:00:00.000Z", UpdatedAt = DateTimeOffset.UnixEpoch,
        };

        return value with { GoogleRaw = Raw(value) };
    }

    private const string ChangedOnGoogleMessage =
        "編集中に Google 側でこの予定が変わりました。こちらの内容で上書きしますか？";

    // ------------------------------------------------------------------
    // 予定
    // ------------------------------------------------------------------

    [Fact]
    public void 開いている間に同期が入れ先を動かしても保存で元に戻さない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        // 入れ先を B に変えてあるが、まだ move を送っていない
        ws.AddEvent(Synced(calendar: "cal-b", googleCalendar: "cal-a"));
        var (vm, editors) = Create(test);

        editors.OnEvent = editor =>
        {
            // 編集画面を開いている間に、同期が events.move を済ませた（内容は変わっていない）
            ws.Events.Upsert(ws.Events.Find("e1")! with { GoogleCalendarId = "cal-b" });
            editor.Title = "定例（変更）";
            return true;
        };

        vm.EditEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        var saved = ws.Events.Find("e1")!;
        Assert.Equal("定例（変更）", saved.Title);

        // 開いた時点の GoogleCalendarId（cal-a）で書き戻すと、移し終えたものを A に戻して
        // しまい、次の同期が「A から B へ」をまた移そうとして 404 になり、作り直しで二重になる
        Assert.Equal("cal-b", saved.GoogleCalendarId);
        Assert.Equal("cal-b", saved.CalendarId);
        Assert.Equal(0, editors.OverwriteAsked);
    }

    [Fact]
    public void 開いている間にGoogle側で変わったら保存の前に確認する()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddEvent(Synced());
        var (vm, editors) = Create(test);

        var shown = 0;
        editors.ConfirmsOverwrite = false;
        editors.OnEvent = editor =>
        {
            if (++shown > 1) return false;

            // 同僚が Google 側で題を変えた。同期が取り込んだ
            var latest = ws.Events.Find("e1")!;
            var theirs = latest with { Title = "定例（同僚が変更）", GoogleUpdated = "2026-09-20T00:00:00.000Z" };
            ws.Events.Upsert(theirs with { GoogleRaw = Raw(theirs) });

            editor.Title = "定例（こちらで変更）";
            return true;
        };

        vm.EditEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        Assert.Equal(1, editors.OverwriteAsked);
        Assert.Equal(ChangedOnGoogleMessage, editors.LastConfirmMessage);

        // 「いいえ」なら保存しない。編集画面に戻る（もう一度出る）
        Assert.Equal(2, shown);
        Assert.Equal("定例（同僚が変更）", ws.Events.Find("e1")!.Title);
    }

    [Fact]
    public void 確認で上書きを選ぶとこちらの内容で保存し結び付きは最新を保つ()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddEvent(Synced());
        var (vm, editors) = Create(test);

        CalendarEvent? theirs = null;
        editors.ConfirmsOverwrite = true;
        editors.OnEvent = editor =>
        {
            var latest = ws.Events.Find("e1")!;
            theirs = latest with { Title = "定例（同僚が変更）", GoogleUpdated = "2026-09-20T00:00:00.000Z" };
            theirs = theirs with { GoogleRaw = Raw(theirs) };
            ws.Events.Upsert(theirs);

            editor.Title = "定例（こちらで変更）";
            return true;
        };

        vm.EditEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        Assert.Equal(1, editors.OverwriteAsked);

        var saved = ws.Events.Find("e1")!;
        Assert.Equal("定例（こちらで変更）", saved.Title);

        // 控えは Google の最新のまま。送れば Google の側もこちらの内容になる
        Assert.Equal(theirs!.GoogleRaw, saved.GoogleRaw);
        Assert.Equal("2026-09-20T00:00:00.000Z", saved.GoogleUpdated);
        Assert.True(EventMapper.NeedsPush(saved));
    }

    [Fact]
    public void 変わっていなければ確認しない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddEvent(Synced());
        var (vm, editors) = Create(test);

        editors.OnEvent = editor => { editor.Title = "定例（変更）"; return true; };

        vm.EditEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        Assert.Equal(0, editors.OverwriteAsked);
        Assert.Equal("定例（変更）", ws.Events.Find("e1")!.Title);
    }

    [Fact]
    public void 開いている間にGoogle側で消えていたら保存せずに知らせる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddEvent(Synced());
        var (vm, editors) = Create(test);

        editors.OnEvent = editor =>
        {
            // Google 側で消されて、同期が手元からも消した
            ws.Events.Delete("e1");
            editor.Title = "定例（変更）";
            return true;
        };

        vm.EditEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        // 保存で行を作り直すと、Google が消したものを手元だけが生き返らせてしまう
        Assert.Null(ws.Events.Find("e1"));
        Assert.Equal(0, ws.Tombstones.Count());
        Assert.Contains("消え", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("保存しませんでした", vm.StatusMessage, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 確認の判断（ViewModel でテストできる形）
    // ------------------------------------------------------------------

    [Fact]
    public void 開いた時点と同じなら競合しない()
    {
        var opened = Synced();

        Assert.Equal(EditConflictKind.None, EditConflict.Check(opened, opened with { }));
    }

    [Fact]
    public void 行が無ければ消えたと判断する()
    {
        Assert.Equal(EditConflictKind.Deleted, EditConflict.Check(Synced(), null));
    }

    [Fact]
    public void 更新時刻が変わっていればGoogle側の変更と判断する()
    {
        var opened = Synced();

        Assert.Equal(
            EditConflictKind.GoogleChanged,
            EditConflict.Check(opened, opened with { GoogleUpdated = "2026-09-20T00:00:00.000Z" }));
    }

    [Fact]
    public void 控えた生データが変わっていればGoogle側の変更と判断する()
    {
        var opened = Synced();

        Assert.Equal(
            EditConflictKind.GoogleChanged,
            EditConflict.Check(opened, opened with { GoogleRaw = """{"id":"g1","summary":"別の題"}""" }));
    }

    [Fact]
    public void 入れ先の移動だけでは競合しない()
    {
        var opened = Synced(calendar: "cal-b", googleCalendar: "cal-a");

        Assert.Equal(
            EditConflictKind.None,
            EditConflict.Check(opened, opened with { GoogleCalendarId = "cal-b" }));
    }

    [Fact]
    public void 手元の中身が変わっていてもGoogle側が同じなら競合しない()
    {
        var opened = Synced();

        // 別の画面（ドラッグなど）でこちらの内容が変わった。Google 側は動いていない
        Assert.Equal(
            EditConflictKind.None,
            EditConflict.Check(opened, opened with { Title = "手元だけの変更" }));
    }

    [Fact]
    public void まだ一度も同期していない予定は競合しない()
    {
        var opened = new CalendarEvent { Id = "e1", Title = "手元だけ", Date = D(2026, 9, 24) };

        Assert.Equal(EditConflictKind.None, EditConflict.Check(opened, opened with { Title = "手元で変更" }));
    }

    // ------------------------------------------------------------------
    // タスク
    // ------------------------------------------------------------------

    private static string TaskRaw(TaskItem value)
    {
        var body = TaskMapper.ToGoogle(value);
        body["id"] = "t1";
        return body.ToJsonString();
    }

    private static TaskItem SyncedTask(string list = "list-a", string googleList = "list-a")
    {
        var value = new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = list,
            GoogleTaskId = "t1", GoogleTaskListId = googleList, Source = "google",
            GoogleUpdated = "2026-09-19T00:00:00.000Z",
            ParentId = "parent-1", Position = "00000000000000000001",
            UpdatedAt = DateTimeOffset.UnixEpoch, CreatedAt = DateTimeOffset.UnixEpoch,
        };

        return value with { GoogleRaw = TaskRaw(value) };
    }

    private const string TaskChangedOnGoogleMessage =
        "編集中に Google 側でこのタスクが変わりました。こちらの内容で上書きしますか？";

    [Fact]
    public void 開いている間に同期がタスクの入れ先を動かしても保存で元に戻さない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(SyncedTask(list: "list-b", googleList: "list-a"));
        var (vm, editors) = Create(test);

        editors.OnTask = editor =>
        {
            ws.Tasks.Upsert(ws.Tasks.Find("t1")! with { GoogleTaskListId = "list-b" });
            editor.Title = "集計（変更）";
            return true;
        };

        vm.EditTaskCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        var saved = ws.Tasks.Find("t1")!;
        Assert.Equal("集計（変更）", saved.Title);
        Assert.Equal("list-b", saved.GoogleTaskListId);
        Assert.Equal("list-b", saved.TaskListId);
    }

    [Fact]
    public void 開いている間にGoogle側でタスクが変わったら保存の前に確認する()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(SyncedTask());
        var (vm, editors) = Create(test);

        var shown = 0;
        editors.ConfirmsOverwrite = false;
        editors.OnTask = editor =>
        {
            if (++shown > 1) return false;

            var theirs = ws.Tasks.Find("t1")! with { Title = "集計（同僚が変更）", GoogleUpdated = "2026-09-20T00:00:00.000Z" };
            ws.Tasks.Upsert(theirs with { GoogleRaw = TaskRaw(theirs) });

            editor.Title = "集計（こちらで変更）";
            return true;
        };

        vm.EditTaskCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        Assert.Equal(1, editors.OverwriteAsked);
        Assert.Equal(TaskChangedOnGoogleMessage, editors.LastConfirmMessage);
        Assert.Equal(2, shown);
        Assert.Equal("集計（同僚が変更）", ws.Tasks.Find("t1")!.Title);
    }

    [Fact]
    public void 開いている間にGoogle側でタスクが消えていたら保存せずに知らせる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(SyncedTask());
        var (vm, editors) = Create(test);

        editors.OnTask = editor =>
        {
            ws.Tasks.Delete("t1");
            editor.Title = "集計（変更）";
            return true;
        };

        vm.EditTaskCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        Assert.Null(ws.Tasks.Find("t1"));
        Assert.Contains("保存しませんでした", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void タスクの編集画面は親子関係と並び順と結び付きを引き継ぐ()
    {
        var source = SyncedTask();
        var editor = new TaskEditorViewModel(source, [new SourceChoice("list-a", "リスト")], D(2026, 9, 24))
        {
            Title = "集計（変更）",
        };

        var model = editor.ToModel();

        // 落とすと、保存のたびに「一度も受け取っていない」に戻って送り直しになり、
        // 次の同期で Google の側の値（親子・並び順）と食い違う
        Assert.Equal(source.GoogleRaw, model.GoogleRaw);
        Assert.Equal("parent-1", model.ParentId);
        Assert.Equal("00000000000000000001", model.Position);
        Assert.Equal("t1", model.GoogleTaskId);
        Assert.Equal("list-a", model.GoogleTaskListId);
        Assert.Equal("2026-09-19T00:00:00.000Z", model.GoogleUpdated);
    }

    [Fact]
    public void タスクを2回続けて編集しても結び付きと親子関係が残る()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(SyncedTask());
        var (vm, editors) = Create(test);

        editors.OnTask = editor => { editor.Title = "集計（1回目）"; return true; };
        vm.EditTaskCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        editors.OnTask = editor => { editor.Title = "集計（2回目）"; return true; };
        vm.EditTaskCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        var saved = ws.Tasks.Find("t1")!;
        Assert.Equal("集計（2回目）", saved.Title);
        Assert.NotNull(saved.GoogleRaw);
        Assert.Equal("parent-1", saved.ParentId);
        Assert.Equal("00000000000000000001", saved.Position);
    }

    // ------------------------------------------------------------------
    // 「Google 上で見つからない」印（項目3の画面側）
    // ------------------------------------------------------------------

    [Fact]
    public void 印が付いた予定の編集画面はその旨を出す()
    {
        var editor = new EventEditorViewModel(Synced() with { GoogleMissing = true }, []);

        Assert.True(editor.IsMissingOnGoogle);
        Assert.NotNull(editor.MissingOnGoogleMessage);
        Assert.Contains("Google", editor.MissingOnGoogleMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ふつうの予定の編集画面は何も出さない()
    {
        var editor = new EventEditorViewModel(Synced(), []);

        Assert.False(editor.IsMissingOnGoogle);
        Assert.Null(editor.MissingOnGoogleMessage);

        // 印が無いのに作り直しを求めても受け付けない（二重にしないため）
        editor.RequestRecreate();
        Assert.False(editor.RecreateRequested);
    }

    [Fact]
    public void 作り直しを選んだときだけ結び付きを外して新規として送る()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddEvent(Synced() with { GoogleMissing = true });
        var (vm, editors) = Create(test);

        editors.OnEvent = editor =>
        {
            Assert.True(editor.IsMissingOnGoogle);
            editor.RequestRecreate();
            return true;
        };

        vm.EditEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        var saved = ws.Events.Find("e1")!;
        Assert.Null(saved.GoogleEventId);
        Assert.Null(saved.GoogleRaw);
        Assert.Null(saved.GoogleCalendarId);
        Assert.False(saved.GoogleMissing);
        Assert.True(EventMapper.NeedsPush(saved));
    }

    [Fact]
    public void 作り直しを選ばずに保存しても結び付きは外れない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddEvent(Synced() with { GoogleMissing = true });
        var (vm, editors) = Create(test);

        editors.OnEvent = editor => { editor.Title = "定例（変更）"; return true; };

        vm.EditEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        var saved = ws.Events.Find("e1")!;
        Assert.Equal("g1", saved.GoogleEventId);
        Assert.True(saved.GoogleMissing);
    }

    [Fact]
    public void 印が付いたタスクも作り直しを選んだときだけ結び付きを外す()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(SyncedTask() with { GoogleMissing = true });
        var (vm, editors) = Create(test);

        editors.OnTask = editor =>
        {
            Assert.True(editor.IsMissingOnGoogle);
            Assert.NotNull(editor.MissingOnGoogleMessage);
            editor.RequestRecreate();
            return true;
        };

        vm.EditTaskCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        var saved = ws.Tasks.Find("t1")!;
        Assert.Null(saved.GoogleTaskId);
        Assert.Null(saved.GoogleRaw);
        Assert.False(saved.GoogleMissing);
        Assert.True(TaskMapper.NeedsPush(saved));
    }

    [Fact]
    public void 印が付いていない予定の作り直しは受け付けない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Synced());

        Assert.False(test.Workspace.RecreateEventOnGoogle("e1"));
        Assert.Equal("g1", test.Workspace.Events.Find("e1")!.GoogleEventId);
    }
}
