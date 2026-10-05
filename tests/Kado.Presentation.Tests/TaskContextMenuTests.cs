using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Mapping;
using Kado.Presentation.Menus;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// タスクの右クリックメニュー（編集・完了・期限を変える・別のリストへ移す・複製・題名をコピー・削除）。
/// <para>
/// 「今日」と「週の始まり」は引数・コンストラクタで渡す。実機の時計には頼らない。
/// テスト用のワークスペースは 2026年9月の稼働日を登録してある（9/21〜23 は休み）。
/// 9/18 は金曜、9/24 は木曜。
/// </para>
/// </summary>
public class TaskContextMenuTests : IDisposable
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private readonly TestWorkspace _test = TestWorkspace.Create();
    private readonly FakeEditorPresenter _editors = new();
    private readonly FakeClipboard _clipboard = new();

    public void Dispose() => _test.Dispose();

    private MainViewModel Create(DateOnly today, DayOfWeek weekStart = DayOfWeek.Sunday) => new(
        _test.Workspace, today, weekStart, editors: _editors, clipboard: _clipboard);

    private static TaskItem Task(string id = "t1", DateOnly? due = null, string? list = "local:mytasks") => new()
    {
        Id = id, Title = "見積もり", Due = due, TaskListId = list,
        Note = "メモ", Url = "https://example.com/spec",
        Attachments = """[{"path":"C:\\資料\\図面.pdf"}]""",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static MenuChoice Due(TaskMenuInfo info, string label) => info.DueChoices.Single(c => c.Label == label);

    // ------------------------------------------------------------------
    // 期限を変える
    // ------------------------------------------------------------------

    [Fact]
    public void 期限の選択肢は今日_明日_次の稼働日_来週の週始め_期限なしの順に並ぶ()
    {
        _test.Workspace.AddTask(Task(due: D(2026, 9, 30)));
        var vm = Create(D(2026, 9, 18));

        var info = vm.TaskMenuFor(_test.Workspace.Tasks.Find("t1"));

        Assert.Equal(
            ["今日", "明日", "次の稼働日", "来週の週始め（最初の稼働日）", "期限なし"],
            info.DueChoices.Select(c => c.Label));
    }

    [Fact]
    public void 次の稼働日と来週の週始めは稼働日データの休みを飛ばす()
    {
        // 9/18 は金曜。土日を除くだけなら次の稼働日は 9/21（月）だが、会社の休みで 9/21〜23 は休み。
        // 稼働日データを優先して 9/24（木）になる
        _test.Workspace.AddTask(Task(due: D(2026, 9, 30)));
        var vm = Create(D(2026, 9, 18));

        var info = vm.TaskMenuFor(_test.Workspace.Tasks.Find("t1"));

        Assert.Equal(new TaskDueRequest("t1", D(2026, 9, 24)), Due(info, "次の稼働日").Parameter);

        // 週は日曜始まり。今週は 9/13〜9/19、来週の頭は 9/20（日）。最初の稼働日は 9/24（木）
        Assert.Equal(new TaskDueRequest("t1", D(2026, 9, 24)), Due(info, "来週の週始め（最初の稼働日）").Parameter);
    }

    [Fact]
    public void 来週の週始めは週の始まりの設定に従う()
    {
        _test.Workspace.AddTask(Task(due: D(2026, 9, 29)));

        // 9/24（木）。月曜始まりなら今週は 9/21〜9/27、来週の頭は 9/28（月）
        var monday = Create(D(2026, 9, 24), DayOfWeek.Monday).TaskMenuFor(_test.Workspace.Tasks.Find("t1"));
        Assert.Equal(D(2026, 9, 28), ((TaskDueRequest)Due(monday, "来週の週始め（最初の稼働日）").Parameter!).Due);

        // 水曜始まりなら今週は 9/23〜9/29、来週の頭は 9/30（水）
        var wednesday = Create(D(2026, 9, 24), DayOfWeek.Wednesday).TaskMenuFor(_test.Workspace.Tasks.Find("t1"));
        Assert.Equal(D(2026, 9, 30), ((TaskDueRequest)Due(wednesday, "来週の週始め（最初の稼働日）").Parameter!).Due);
    }

    [Fact]
    public void いまの期限と同じ項目は灰色になる()
    {
        _test.Workspace.AddTask(Task(due: D(2026, 9, 24)));
        var vm = Create(D(2026, 9, 24));

        var info = vm.TaskMenuFor(_test.Workspace.Tasks.Find("t1"));

        Assert.False(Due(info, "今日").IsEnabled);
        Assert.Equal("いまの期限です", Due(info, "今日").ToolTip);
        Assert.True(Due(info, "明日").IsEnabled);
        Assert.True(Due(info, "期限なし").IsEnabled);
    }

    [Fact]
    public void 期限が無いタスクは期限なしが灰色になる()
    {
        _test.Workspace.AddTask(Task(due: null));
        var vm = Create(D(2026, 9, 24));

        var info = vm.TaskMenuFor(_test.Workspace.Tasks.Find("t1"));

        Assert.False(Due(info, "期限なし").IsEnabled);
        Assert.True(Due(info, "今日").IsEnabled);
    }

    [Fact]
    public void 期限を選ぶと変わり_元に戻せる()
    {
        _test.Workspace.AddTask(Task(due: D(2026, 9, 24)));
        var vm = Create(D(2026, 9, 24));
        var choice = Due(vm.TaskMenuFor(_test.Workspace.Tasks.Find("t1")), "明日");

        choice.Command.Execute(choice.Parameter);

        Assert.Equal(D(2026, 9, 25), _test.Workspace.Tasks.Find("t1")!.Due);
        Assert.Contains("9/25(金)", vm.StatusMessage, StringComparison.Ordinal);

        Assert.Equal("タスクの期限の変更", _test.Workspace.Undo.UndoDescription);
        _test.Workspace.UndoLast();
        Assert.Equal(D(2026, 9, 24), _test.Workspace.Tasks.Find("t1")!.Due);
    }

    [Fact]
    public void 期限の変更はドラッグで期限を動かしたときと同じ結果になる()
    {
        _test.Workspace.AddTask(Task("viaMenu", due: D(2026, 9, 24)));
        _test.Workspace.AddTask(Task("viaDrag", due: D(2026, 9, 24)));
        var vm = Create(D(2026, 9, 24));

        var choice = Due(vm.TaskMenuFor(_test.Workspace.Tasks.Find("viaMenu")), "明日");
        choice.Command.Execute(choice.Parameter);
        vm.MoveTaskTo("viaDrag", D(2026, 9, 25));

        var menu = _test.Workspace.Tasks.Find("viaMenu")!;
        var drag = _test.Workspace.Tasks.Find("viaDrag")!;

        // 並び順は、追加した順に振られる（動かしても変わらない）ので比べない
        Assert.Equal(
            drag with { Id = "viaMenu", UpdatedAt = default, CreatedAt = default, SortOrder = 0 },
            menu with { UpdatedAt = default, CreatedAt = default, SortOrder = 0 });
    }

    [Fact]
    public void 期限なしを選ぶと繰り返しも外れ_元に戻すと繰り返しも戻る()
    {
        _test.Workspace.AddTask(Task(due: D(2026, 9, 21)) with { Repeat = "FREQ=WEEKLY;BYDAY=MO" });
        var vm = Create(D(2026, 9, 24));
        var choice = Due(vm.TaskMenuFor(_test.Workspace.Tasks.Find("t1")), "期限なし");

        choice.Command.Execute(choice.Parameter);

        var after = _test.Workspace.Tasks.Find("t1")!;
        Assert.Null(after.Due);
        Assert.Null(after.Repeat);
        Assert.Contains("繰り返しも外れました", vm.StatusMessage, StringComparison.Ordinal);

        Assert.Equal("タスクの期限を外す", _test.Workspace.Undo.UndoDescription);
        _test.Workspace.UndoLast();

        var restored = _test.Workspace.Tasks.Find("t1")!;
        Assert.Equal(D(2026, 9, 21), restored.Due);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", restored.Repeat);
    }

    [Fact]
    public void 期限を変えると暦どおりの繰り返しは新しい期限日の曜日に指定し直される()
    {
        _test.Workspace.AddTask(Task(due: D(2026, 9, 21)) with { Repeat = "FREQ=WEEKLY;BYDAY=MO" });
        var vm = Create(D(2026, 9, 24));
        var choice = Due(vm.TaskMenuFor(_test.Workspace.Tasks.Find("t1")), "今日");

        choice.Command.Execute(choice.Parameter);

        // 9/24 は木曜日
        Assert.Equal("FREQ=WEEKLY;BYDAY=TH", _test.Workspace.Tasks.Find("t1")!.Repeat);
    }

    [Fact]
    public void 完了済みのタスクの期限も変えられる_完了のまま()
    {
        _test.Workspace.AddTask(Task(due: D(2026, 9, 24)) with { IsDone = true });
        var vm = Create(D(2026, 9, 24));
        var choice = Due(vm.TaskMenuFor(_test.Workspace.Tasks.Find("t1")), "明日");

        choice.Command.Execute(choice.Parameter);

        var after = _test.Workspace.Tasks.Find("t1")!;
        Assert.True(after.IsDone);
        Assert.Equal(D(2026, 9, 25), after.Due);
    }

    // ------------------------------------------------------------------
    // 別のリストへ移す
    // ------------------------------------------------------------------

    [Fact]
    public void 移し先は今のリストを除き_左パネルと同じ並びと色で並ぶ()
    {
        var a = _test.Workspace.CreateTaskList("A");
        var b = _test.Workspace.CreateTaskList("B");
        _test.Workspace.AddTask(Task(list: a.Id));
        var vm = Create(D(2026, 9, 24));

        var info = vm.TaskMenuFor(_test.Workspace.Tasks.Find("t1"));

        var names = info.ListChoices.Select(c => c.Label).ToArray();
        Assert.DoesNotContain("A", names);
        Assert.Contains("B", names);
        Assert.Contains(CalendarWorkspace.DefaultTaskListName, names);

        var panel = vm.SourceLists.TaskLists.Select(t => t.Name).Where(names.Contains).ToArray();
        Assert.Equal(panel, names);

        Assert.Equal(
            vm.SourceLists.TaskLists.Single(t => t.Id == b.Id).SwatchColor,
            info.ListChoices.Single(c => c.Label == "B").Color);
        Assert.True(info.CanMoveToList);
    }

    [Fact]
    public void リストを選ぶと入れ先だけが変わり_元に戻せる()
    {
        var a = _test.Workspace.CreateTaskList("A");
        var b = _test.Workspace.CreateTaskList("B");
        _test.Workspace.AddTask(Task(list: a.Id) with { GoogleTaskId = "g1", GoogleTaskListId = "gl-a" });
        var vm = Create(D(2026, 9, 24));

        // Google と結び付いたタスクにはローカルのリストを出さない（編集画面の候補と同じ決まり）。ここは手元のタスクで試す
        _test.Workspace.Tasks.Upsert(_test.Workspace.Tasks.Find("t1")! with { GoogleTaskId = null, GoogleTaskListId = null });

        var choice = vm.TaskMenuFor(_test.Workspace.Tasks.Find("t1")).ListChoices.Single(c => c.Label == "B");
        choice.Command.Execute(choice.Parameter);

        Assert.Equal(b.Id, _test.Workspace.Tasks.Find("t1")!.TaskListId);
        Assert.Equal("「B」へ移しました", vm.StatusMessage);

        Assert.Equal("タスクを別のリストへ移す", _test.Workspace.Undo.UndoDescription);
        _test.Workspace.UndoLast();
        Assert.Equal(a.Id, _test.Workspace.Tasks.Find("t1")!.TaskListId);
    }

    [Fact]
    public void リストの移動は編集画面でリストを変えて保存したときと同じ結果になる()
    {
        var a = _test.Workspace.CreateTaskList("A");
        var b = _test.Workspace.CreateTaskList("B");
        _test.Workspace.AddTask(Task("viaMenu", list: a.Id));
        _test.Workspace.AddTask(Task("viaEditor", list: a.Id));
        var vm = Create(D(2026, 9, 24));

        _editors.OnTask = editor =>
        {
            editor.TaskListId = b.Id;
            return true;
        };
        vm.EditTaskEntryCommand.Execute(_test.Workspace.Tasks.Find("viaEditor"));

        var choice = vm.TaskMenuFor(_test.Workspace.Tasks.Find("viaMenu")).ListChoices.Single(c => c.Label == "B");
        choice.Command.Execute(choice.Parameter);

        var menu = _test.Workspace.Tasks.Find("viaMenu")!;
        var editor = _test.Workspace.Tasks.Find("viaEditor")!;

        Assert.Equal(
            editor with { Id = "viaMenu", UpdatedAt = default, CreatedAt = default, SortOrder = 0 },
            menu with { UpdatedAt = default, CreatedAt = default, SortOrder = 0 });
    }

    [Fact]
    public void サブタスクは移せず_項目を灰色にして理由を出す()
    {
        var b = _test.Workspace.CreateTaskList("B");
        _test.Workspace.AddTask(Task("parent"));
        _test.Workspace.AddTask(Task("child") with { ParentId = "parent" });
        var vm = Create(D(2026, 9, 24));

        var info = vm.TaskMenuFor(_test.Workspace.Tasks.Find("child"));

        Assert.True(info.CanMoveToList);
        var choice = info.ListChoices.Single(c => c.Label == "B");
        Assert.False(choice.IsEnabled);
        Assert.Equal(TaskMapper.ChildMoveReason, choice.ToolTip);

        // 押されても移さない
        choice.Command.Execute(choice.Parameter);
        Assert.Equal("local:mytasks", _test.Workspace.Tasks.Find("child")!.TaskListId);
        Assert.Equal(TaskMapper.ChildMoveReason, vm.StatusMessage);
        Assert.NotEqual(b.Id, _test.Workspace.Tasks.Find("child")!.TaskListId);
    }

    [Fact]
    public void サブタスクを持つタスクも移せず_項目を灰色にして理由を出す()
    {
        _test.Workspace.CreateTaskList("B");
        _test.Workspace.AddTask(Task("parent"));
        _test.Workspace.AddTask(Task("child") with { ParentId = "parent" });
        var vm = Create(D(2026, 9, 24));

        var choice = vm.TaskMenuFor(_test.Workspace.Tasks.Find("parent")).ListChoices.Single(c => c.Label == "B");

        Assert.False(choice.IsEnabled);
        Assert.Equal(TaskMapper.ParentMoveReason, choice.ToolTip);
    }

    [Fact]
    public void 移し先が無ければ親の項目を灰色にして理由を出す()
    {
        _test.Workspace.AddTask(Task());
        var vm = Create(D(2026, 9, 24));

        // 起動時に作られる「マイタスク」だけ
        var info = vm.TaskMenuFor(_test.Workspace.Tasks.Find("t1"));

        Assert.False(info.CanMoveToList);
        Assert.Empty(info.ListChoices);
        Assert.False(string.IsNullOrEmpty(info.ListMoveDisabledReason));
    }

    // ------------------------------------------------------------------
    // 複製
    // ------------------------------------------------------------------

    [Fact]
    public void 複製は題名メモ期限リストURL添付繰り返しを写した未完了のタスクを作る()
    {
        var a = _test.Workspace.CreateTaskList("A");
        _test.Workspace.AddTask(Task(due: D(2026, 9, 28), list: a.Id) with { Repeat = "FREQ=WEEKLY;BYDAY=MO" });
        var vm = Create(D(2026, 9, 24));

        vm.DuplicateTaskCommand.Execute(_test.Workspace.Tasks.Find("t1"));

        var copy = Assert.Single(_test.Workspace.Tasks.All(), t => t.Id != "t1");
        Assert.Equal("見積もり", copy.Title);
        Assert.Equal("メモ", copy.Note);
        Assert.Equal(D(2026, 9, 28), copy.Due);
        Assert.Equal(a.Id, copy.TaskListId);
        Assert.Equal("https://example.com/spec", copy.Url);
        Assert.Equal("""[{"path":"C:\\資料\\図面.pdf"}]""", copy.Attachments);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", copy.Repeat);
        Assert.False(copy.IsDone);
        Assert.Null(copy.CompletedAt);
        Assert.Equal("複製しました", vm.StatusMessage);
        Assert.Null(_editors.LastTaskEditor);
    }

    [Fact]
    public void 完了済みのタスクを複製すると未完了になる()
    {
        _test.Workspace.AddTask(Task(due: D(2026, 9, 24)) with
        {
            IsDone = true, CompletedAt = DateTimeOffset.UnixEpoch,
        });
        var vm = Create(D(2026, 9, 24));

        vm.DuplicateTaskCommand.Execute(_test.Workspace.Tasks.Find("t1"));

        var copy = Assert.Single(_test.Workspace.Tasks.All(), t => t.Id != "t1");
        Assert.False(copy.IsDone);
        Assert.Null(copy.CompletedAt);
    }

    [Fact]
    public void サブタスクを複製すると親なしのタスクになり_Googleの結び付きは持たない()
    {
        _test.Workspace.AddTask(Task("parent"));
        _test.Workspace.AddTask(Task("child", due: D(2026, 9, 24)) with
        {
            ParentId = "parent", GoogleTaskId = "g2", GoogleTaskListId = "gl", GoogleRaw = "{}",
            GoogleUpdated = "x", Position = "0002",
        });
        var vm = Create(D(2026, 9, 24));

        vm.DuplicateTaskCommand.Execute(_test.Workspace.Tasks.Find("child"));

        var copy = Assert.Single(_test.Workspace.Tasks.All(), t => t.Id is not ("parent" or "child"));
        Assert.Null(copy.ParentId);
        Assert.Null(copy.GoogleTaskId);
        Assert.Null(copy.GoogleTaskListId);
        Assert.Null(copy.GoogleRaw);
        Assert.Null(copy.GoogleUpdated);
        Assert.Null(copy.Position);

        // 元のサブタスクは触らない
        var original = _test.Workspace.Tasks.Find("child")!;
        Assert.Equal("parent", original.ParentId);
        Assert.Equal("g2", original.GoogleTaskId);
    }

    [Fact]
    public void 期限の無いタスクの複製は繰り返しを持たない()
    {
        _test.Workspace.AddTask(Task(due: null) with { Repeat = "FREQ=WEEKLY;BYDAY=MO" });
        var vm = Create(D(2026, 9, 24));

        vm.DuplicateTaskCommand.Execute(_test.Workspace.Tasks.Find("t1"));

        Assert.Null(Assert.Single(_test.Workspace.Tasks.All(), t => t.Id != "t1").Repeat);
    }

    [Fact]
    public void 複製は同じ期限日の末尾に置かれ_元に戻すと消える()
    {
        _test.Workspace.AddTask(Task("a", due: D(2026, 9, 24)));
        _test.Workspace.AddTask(Task("b", due: D(2026, 9, 24)));
        var vm = Create(D(2026, 9, 24));

        vm.DuplicateTaskCommand.Execute(_test.Workspace.Tasks.Find("a"));

        var copy = _test.Workspace.Tasks.All().Single(t => t.Id is not ("a" or "b"));
        Assert.True(copy.SortOrder > _test.Workspace.Tasks.Find("b")!.SortOrder);

        Assert.Equal("タスクの複製", _test.Workspace.Undo.UndoDescription);
        _test.Workspace.UndoLast();
        Assert.Equal(2, _test.Workspace.Tasks.All().Count);
    }

    // ------------------------------------------------------------------
    // 情報の覚え
    // ------------------------------------------------------------------

    [Fact]
    public void 変わっていなければ同じ情報を返し_期限や今日が変わったら作り直す()
    {
        _test.Workspace.AddTask(Task(due: D(2026, 9, 24)));
        var vm = Create(D(2026, 9, 24));
        var stored = _test.Workspace.Tasks.Find("t1");

        var first = vm.TaskMenuFor(stored);
        Assert.Same(first, vm.TaskMenuFor(stored));
        Assert.False(Due(first, "今日").IsEnabled);

        // 期限が変わった
        _test.Workspace.UpdateTask(_test.Workspace.Tasks.Find("t1")! with { Due = D(2026, 9, 25) });
        var second = vm.TaskMenuFor(_test.Workspace.Tasks.Find("t1"));
        Assert.NotSame(first, second);
        Assert.True(Due(second, "今日").IsEnabled);
        Assert.False(Due(second, "明日").IsEnabled);

        // 日付が変わった（起動しっぱなしで日をまたぐ）。「今日」の指す日が変わる
        vm.Today = D(2026, 9, 25);
        var third = vm.TaskMenuFor(_test.Workspace.Tasks.Find("t1"));
        Assert.False(Due(third, "今日").IsEnabled);
    }

    // ------------------------------------------------------------------
    // 題名をコピー
    // ------------------------------------------------------------------

    [Fact]
    public void 題名をコピーは期限があれば期限と題名_無ければ題名だけ()
    {
        _test.Workspace.AddTask(Task("a", due: D(2026, 10, 6)));
        _test.Workspace.AddTask(Task("b", due: null));
        var vm = Create(D(2026, 9, 24));

        vm.CopyTaskTitleCommand.Execute(_test.Workspace.Tasks.Find("a"));
        Assert.Equal("10/6(火) 期限 見積もり", _clipboard.Text);
        Assert.Equal("コピーしました", vm.StatusMessage);

        vm.CopyTaskTitleCommand.Execute(_test.Workspace.Tasks.Find("b"));
        Assert.Equal("見積もり", _clipboard.Text);
    }

    // ------------------------------------------------------------------
    // 完了・編集・削除
    // ------------------------------------------------------------------

    [Fact]
    public void 完了の項目の文言は完了と未完了で切り替わる()
    {
        _test.Workspace.AddTask(Task("a", due: D(2026, 9, 24)));
        var vm = Create(D(2026, 9, 24));

        Assert.Equal("完了にする", vm.TaskMenuFor(_test.Workspace.Tasks.Find("a")).DoneLabel);

        vm.ToggleTaskEntryDoneCommand.Execute(_test.Workspace.Tasks.Find("a"));

        Assert.True(_test.Workspace.Tasks.Find("a")!.IsDone);
        Assert.Equal("完了を取り消す", vm.TaskMenuFor(_test.Workspace.Tasks.Find("a")).DoneLabel);

        vm.ToggleTaskEntryDoneCommand.Execute(_test.Workspace.Tasks.Find("a"));
        Assert.False(_test.Workspace.Tasks.Find("a")!.IsDone);
    }

    [Fact]
    public void 繰り返しのタスクを完了にすると次の回ができる()
    {
        _test.Workspace.AddTask(Task(due: D(2026, 9, 21)) with { Repeat = "FREQ=WEEKLY;BYDAY=MO" });
        var vm = Create(D(2026, 9, 24));

        vm.ToggleTaskEntryDoneCommand.Execute(_test.Workspace.Tasks.Find("t1"));

        Assert.Equal(2, _test.Workspace.Tasks.All().Count);
        Assert.Contains("次は", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void 編集と削除はどの画面の行からでも効く()
    {
        _test.Workspace.AddTask(Task("a", due: D(2026, 9, 24)));
        _test.Workspace.AddTask(Task("b", due: D(2026, 9, 24)));
        var vm = Create(D(2026, 9, 24));

        // 右ペインの行（TaskListItemViewModel）と月のチップ（ScheduledTask）
        var row = vm.SelectedDay.Tasks.Single(t => t.Id == "a");
        var chip = vm.Month.Cells.Single(c => c.Date == D(2026, 9, 24)).Tasks.Single(t => t.Id == "b");

        _editors.OnTask = _ => false;
        vm.EditTaskEntryCommand.Execute(row);
        Assert.Equal("見積もり", _editors.LastTaskEditor!.Title);

        vm.DeleteTaskEntryCommand.Execute(row);
        vm.DeleteTaskEntryCommand.Execute(chip);

        Assert.Empty(_test.Workspace.Tasks.All());
    }

    [Fact]
    public void どの画面の行でも同じタスクとして読み取れる()
    {
        _test.Workspace.AddTask(Task("a", due: D(2026, 9, 24)));
        var vm = Create(D(2026, 9, 24));

        Assert.Equal("a", EntryRefs.TaskOf(vm.SelectedDay.Tasks.Single()));
        Assert.Equal("a", EntryRefs.TaskOf(vm.Month.Cells.Single(c => c.Date == D(2026, 9, 24)).Tasks.Single()));
        Assert.Equal("a", EntryRefs.TaskOf(_test.Workspace.Tasks.Find("a")));
        Assert.Null(EntryRefs.TaskOf(new object()));
        Assert.Null(EntryRefs.TaskOf(null));
    }

    [Fact]
    public void タスクが見つからなければ何も壊さず見つかりませんでしたと伝える()
    {
        var vm = Create(D(2026, 9, 24));
        var missing = new TaskItem { Id = "missing", Title = "x" };

        vm.DuplicateTaskCommand.Execute(missing);
        Assert.Equal("タスクが見つかりませんでした", vm.StatusMessage);

        vm.CopyTaskTitleCommand.Execute(missing);
        Assert.Null(_clipboard.Text);

        Assert.Same(TaskMenuInfo.Unavailable, vm.TaskMenuFor(missing));
    }

    [Fact]
    public void 右ペインの並べ替えは削除の上に残り_行の外では効かない()
    {
        // 右ペインの「上へ・下へ移動」は、これまでどおり同じ期限日の中の入れ替え
        _test.Workspace.AddTask(Task("a", due: D(2026, 9, 24)));
        _test.Workspace.AddTask(Task("b", due: D(2026, 9, 24)));
        var vm = Create(D(2026, 9, 24));

        var first = vm.SelectedDay.Tasks.First();
        Assert.False(vm.MoveTaskUpCommand.CanExecute(first));
        Assert.True(vm.MoveTaskDownCommand.CanExecute(first));
    }
}
