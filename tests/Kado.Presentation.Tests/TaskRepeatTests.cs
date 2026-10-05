using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// タスクの繰り返し（Kado 独自）を、完了にしたときに次の回へつなげる流れ。
/// <para>
/// 「今日」と「週の始まり」は引数・コンストラクタで渡す。実機の時計には頼らない。
/// テスト用のワークスペースは 2026年9月の稼働日を登録してある（9/21〜23 は休み）。
/// 9/24 は木曜、9/21 は月曜。
/// </para>
/// </summary>
public class TaskRepeatTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly DateOnly Today = D(2026, 9, 24);

    private const string WeeklyMonday = "FREQ=WEEKLY;BYDAY=MO";

    private static TaskItem Repeating(string id = "t1", DateOnly? due = null, string? repeat = WeeklyMonday) => new()
    {
        Id = id,
        Title = "週報",
        Due = due ?? D(2026, 9, 21),
        Note = "メモ",
        Url = "https://example.com/spec",
        Attachments = """[{"path":"C:\\資料\\図面.pdf"}]""",
        TaskListId = "local:mytasks",
        Repeat = repeat,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    // ------------------------------------------------------------------
    // 完了にする（チェック・チップ）
    // ------------------------------------------------------------------

    [Fact]
    public void 繰り返しのタスクを完了にすると次の回ができ完了したほうから繰り返しが外れる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating());

        var result = ws.ToggleTask("t1", Today);

        Assert.Equal(new TaskToggleResult(true, D(2026, 9, 28)), result);

        var completed = ws.Tasks.Find("t1")!;
        Assert.True(completed.IsDone);
        Assert.NotNull(completed.CompletedAt);
        Assert.Null(completed.Repeat);

        var next = ws.Tasks.All().Single(t => t.Id != "t1");
        Assert.False(next.IsDone);
        Assert.Equal(D(2026, 9, 28), next.Due);
        Assert.Equal("週報", next.Title);
        Assert.Equal("メモ", next.Note);
        Assert.Equal("local:mytasks", next.TaskListId);
        Assert.Equal("https://example.com/spec", next.Url);
        Assert.Equal("""[{"path":"C:\\資料\\図面.pdf"}]""", next.Attachments);
        Assert.Equal(WeeklyMonday, next.Repeat);
    }

    [Fact]
    public void 次の回はGoogleとの結び付きを持たず並び順は同じ期限日の末尾()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        // 次の回の期限日 9/28 に、すでに2件ある
        ws.AddTask(new TaskItem { Id = "a", Title = "先客1", Due = D(2026, 9, 28) });
        ws.AddTask(new TaskItem { Id = "b", Title = "先客2", Due = D(2026, 9, 28) });
        ws.AddTask(Repeating() with
        {
            GoogleTaskId = "g1", GoogleTaskListId = "@default", GoogleRaw = "{}", GoogleUpdated = "x", Position = "0001",
        });

        ws.ToggleTask("t1", Today);

        var next = ws.Tasks.All().Single(t => t.Title == "週報" && !t.IsDone);
        Assert.Null(next.GoogleTaskId);
        Assert.Null(next.GoogleTaskListId);
        Assert.Null(next.GoogleRaw);
        Assert.Null(next.GoogleUpdated);
        Assert.Null(next.Position);
        Assert.NotEqual("t1", next.Id);

        // 並び順は既存の新規追加と同じ決め方（同じ期限日の末尾）。作成日時は今
        Assert.Equal(2, next.SortOrder);
        Assert.True(DateTimeOffset.Now - next.CreatedAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void 完了にして次の回を作るまでが1つの操作として元に戻りやり直せる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating());

        ws.ToggleTask("t1", Today);
        Assert.Equal(2, ws.Tasks.Count());

        // 1回の元に戻すで、完了が戻り、繰り返しが戻り、次の回が消える
        ws.UndoLast();

        var restored = Assert.Single(ws.Tasks.All());
        Assert.Equal("t1", restored.Id);
        Assert.False(restored.IsDone);
        Assert.Null(restored.CompletedAt);
        Assert.Equal(WeeklyMonday, restored.Repeat);

        // やり直しで、また完了になり、次の回ができる（繰り返しは外れる）
        ws.RedoLast();

        Assert.Equal(2, ws.Tasks.Count());
        Assert.True(ws.Tasks.Find("t1")!.IsDone);
        Assert.Null(ws.Tasks.Find("t1")!.Repeat);
        Assert.Equal(D(2026, 9, 28), ws.Tasks.All().Single(t => t.Id != "t1").Due);
    }

    [Fact]
    public void 元に戻す説明は完了と次の回の作成を伝える()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating());

        ws.ToggleTask("t1", Today);

        Assert.Equal("タスクを完了にする（次の回を作成）", ws.Undo.UndoDescription);
    }

    [Fact]
    public void 同期済みの次の回を元に戻すと消したことを記録してGoogleからも消える()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating());

        ws.ToggleTask("t1", Today);

        // 同期が次の回を Google に送って、結び付けた
        var next = ws.Tasks.All().Single(t => t.Id != "t1");
        ws.Tasks.Upsert(next with { GoogleTaskId = "g2", GoogleTaskListId = "@default" });

        ws.UndoLast();

        Assert.Single(ws.Tasks.All());
        var tombstone = ws.Tombstones.Find(next.Id, TombstoneRepository.TaskKind);
        Assert.NotNull(tombstone);
        Assert.Equal("g2", tombstone!.GoogleId);
    }

    [Fact]
    public void 完了を取り消しても次の回は消えず何も作らない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating());
        ws.ToggleTask("t1", Today);

        // 完了 → 未完了
        var result = ws.ToggleTask("t1", Today);

        Assert.Equal(new TaskToggleResult(false, null), result);
        Assert.Equal(2, ws.Tasks.Count());
        Assert.False(ws.Tasks.Find("t1")!.IsDone);

        // 取り消して完了し直しても、繰り返しはもう外れているので二重にならない
        Assert.Null(ws.Tasks.Find("t1")!.Repeat);
        var again = ws.ToggleTask("t1", Today);

        Assert.Equal(new TaskToggleResult(true, null), again);
        Assert.Equal(2, ws.Tasks.Count());
    }

    [Fact]
    public void 繰り返しの無いタスクは今までどおり完了にするだけ()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating(repeat: null));

        var result = ws.ToggleTask("t1", Today);

        Assert.Equal(new TaskToggleResult(true, null), result);
        Assert.Single(ws.Tasks.All());
        Assert.Equal("タスクを完了にする", ws.Undo.UndoDescription);
    }

    [Fact]
    public void 読めない繰り返しは次の回を作らず文字列も消さない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating(repeat: "こわれた文字列"));

        var result = ws.ToggleTask("t1", Today);

        Assert.Equal(new TaskToggleResult(true, null), result);
        var only = Assert.Single(ws.Tasks.All());
        Assert.True(only.IsDone);
        Assert.Equal("こわれた文字列", only.Repeat);
    }

    [Fact]
    public void サブタスクと期限の無いタスクは次の回を作らない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating("sub") with { ParentId = "parent" });
        ws.AddTask(Repeating("nodue") with { Due = null });

        Assert.Equal(new TaskToggleResult(true, null), ws.ToggleTask("sub", Today));
        Assert.Equal(new TaskToggleResult(true, null), ws.ToggleTask("nodue", Today));
        Assert.Equal(2, ws.Tasks.Count());
    }

    [Fact]
    public void いないタスクの切り替えは何も起きない()
    {
        using var test = TestWorkspace.Create();

        Assert.Null(test.Workspace.ToggleTask("いない", Today));
        Assert.False(test.Workspace.ToggleTaskDone("いない"));
    }

    // ------------------------------------------------------------------
    // 稼働日と週の始まり
    // ------------------------------------------------------------------

    [Fact]
    public void 月末の最後の稼働日は登録した稼働日で決まる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating(due: D(2026, 8, 31), repeat: "X-KADO=MONTH-LAST-WORKDAY"));

        // 9月の最後の稼働日は 9/30(水)
        Assert.Equal(D(2026, 9, 30), ws.ToggleTask("t1", D(2026, 8, 31))!.NextDue);
    }

    [Fact]
    public void 週始めは会社の休みを飛ばした最初の稼働日()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating(due: D(2026, 9, 14), repeat: "X-KADO=WEEK-FIRST-WORKDAY"));

        // 9/20(日)始まりの週は 9/21〜23 が休み。最初の稼働日は 9/24(木)
        Assert.Equal(D(2026, 9, 24), ws.ToggleTask("t1", D(2026, 9, 14))!.NextDue);
    }

    [Fact]
    public void 週の区切りは渡した週の始まりに従う()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating(due: D(2026, 9, 14), repeat: "X-KADO=WEEK-FIRST-WORKDAY"));

        // 木曜始まりなら、次の週は 9/17(木)〜9/23(水)。最初の稼働日は 9/17
        Assert.Equal(D(2026, 9, 17), ws.ToggleTask("t1", D(2026, 9, 14), DayOfWeek.Thursday)!.NextDue);
    }

    [Fact]
    public void 登録の無い期間は土日祝を除いた日を稼働日とみなす()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating(due: D(2026, 12, 1), repeat: "X-KADO=MONTH-FIRST-WORKDAY"));

        // 2027年1月は稼働日を登録していない。1/1(金)は元日、1/2・1/3 は土日 → 1/4(月)
        Assert.Equal(D(2027, 1, 4), ws.ToggleTask("t1", D(2026, 12, 1))!.NextDue);
    }

    // ------------------------------------------------------------------
    // 編集画面で完了にして保存（UpdateTask）
    // ------------------------------------------------------------------

    [Fact]
    public void 編集して完了にして保存しても次の回ができ1つの操作で戻る()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating());

        var saved = ws.UpdateTask(
            Repeating() with { IsDone = true, CompletedAt = DateTimeOffset.Now }, out var nextDue, Today);

        Assert.True(saved);
        Assert.Equal(D(2026, 9, 28), nextDue);
        Assert.Equal(2, ws.Tasks.Count());
        Assert.Null(ws.Tasks.Find("t1")!.Repeat);

        ws.UndoLast();

        var restored = Assert.Single(ws.Tasks.All());
        Assert.False(restored.IsDone);
        Assert.Equal(WeeklyMonday, restored.Repeat);
    }

    [Fact]
    public void 題名だけ直して保存しても次の回は作らない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating());

        ws.UpdateTask(Repeating() with { Title = "週報（直した）" }, out var nextDue, Today);

        Assert.Null(nextDue);
        Assert.Single(ws.Tasks.All());
        Assert.Equal(WeeklyMonday, ws.Tasks.Find("t1")!.Repeat);
    }

    [Fact]
    public void すでに完了のタスクを直して保存しても次の回は作らない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating() with { IsDone = true, CompletedAt = DateTimeOffset.Now });

        ws.UpdateTask(Repeating() with { IsDone = true, Title = "直した" }, out var nextDue, Today);

        Assert.Null(nextDue);
        Assert.Single(ws.Tasks.All());
    }

    [Fact]
    public void 保存のとき期限を消したら繰り返しも外れる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating());

        ws.UpdateTask(Repeating() with { Due = null });

        Assert.Null(ws.Tasks.Find("t1")!.Repeat);
    }

    [Fact]
    public void 保存のとき期限日を変えたら暦どおりの指定を指定し直す()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating());

        // 月曜 → 水曜（ドラッグでの移動もここを通る）
        ws.UpdateTask(Repeating(due: D(2026, 9, 23)));

        Assert.Equal("FREQ=WEEKLY;BYDAY=WE", ws.Tasks.Find("t1")!.Repeat);
    }

    [Fact]
    public void サブタスクは保存のとき繰り返しが外れる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(Repeating() with { Repeat = null, ParentId = "parent" });

        ws.UpdateTask(Repeating() with { ParentId = "parent" });

        Assert.Null(ws.Tasks.Find("t1")!.Repeat);
    }

    // ------------------------------------------------------------------
    // 画面（MainViewModel）
    // ------------------------------------------------------------------

    private static (MainViewModel Vm, FakeEditorPresenter Editors) CreateVm(
        TestWorkspace test, DayOfWeek weekStart = DayOfWeek.Sunday)
    {
        var editors = new FakeEditorPresenter();
        return (new MainViewModel(test.Workspace, today: Today, weekStart: weekStart, editors: editors), editors);
    }

    [Fact]
    public void 右ペインのチェックで完了にすると次の期限を知らせる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(Repeating(due: Today, repeat: "FREQ=WEEKLY;BYDAY=TH"));
        var (vm, _) = CreateVm(test);

        vm.ToggleTaskDoneCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        Assert.Equal("タスクを完了にしました。次は 10/1(木)", vm.StatusMessage);
        Assert.Equal(2, test.Workspace.Tasks.Count());

        // 元に戻すと 1手で戻る
        vm.UndoCommand.Execute(null);
        Assert.Single(test.Workspace.Tasks.All());
    }

    [Fact]
    public void チップの完了にするでも次の期限を知らせる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(Repeating(due: Today, repeat: "FREQ=DAILY"));
        var (vm, _) = CreateVm(test);

        var chip = Assert.Single(vm.Month.Cells.Single(c => c.Date == Today).AllTasks);
        vm.ToggleTaskChipDoneCommand.Execute(chip);

        Assert.Equal("タスクを完了にしました。次は 9/25(金)", vm.StatusMessage);
        Assert.Equal(2, test.Workspace.Tasks.Count());
    }

    [Fact]
    public void 編集画面で完了にして保存しても次の期限を知らせる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(Repeating(due: Today, repeat: "FREQ=WEEKLY;BYDAY=TH"));
        var (vm, editors) = CreateVm(test);

        editors.OnTask = editor => { editor.IsDone = true; return true; };
        vm.EditTaskCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        Assert.Equal("タスクを完了にしました。次は 10/1(木)", vm.StatusMessage);

        var all = test.Workspace.Tasks.All();
        Assert.Equal(2, all.Count);
        Assert.Null(all.Single(t => t.Id == "t1").Repeat);
        Assert.Equal(D(2026, 10, 1), all.Single(t => t.Id != "t1").Due);
    }

    [Fact]
    public void 編集画面で題名だけ直した保存は今までのメッセージ()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(Repeating(due: Today, repeat: "FREQ=WEEKLY;BYDAY=TH"));
        var (vm, editors) = CreateVm(test);

        editors.OnTask = editor => { editor.Title = "直した"; return true; };
        vm.EditTaskCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        Assert.Equal("タスクを変更しました", vm.StatusMessage);
        Assert.Single(test.Workspace.Tasks.All());
    }

    [Fact]
    public void 完了の取り消しは次の回を作らず今までのメッセージ()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(Repeating(due: Today, repeat: null) with { IsDone = true, CompletedAt = DateTimeOffset.Now });
        var (vm, _) = CreateVm(test);
        vm.SelectedDate = DateOnly.FromDateTime(DateTime.Today);

        vm.ToggleTaskDoneCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        Assert.Equal("タスクの完了を取り消しました", vm.StatusMessage);
    }

    [Fact]
    public void 繰り返しのないタスクは今までのメッセージ()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(Repeating(due: Today, repeat: null));
        var (vm, _) = CreateVm(test);

        vm.ToggleTaskDoneCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        Assert.Equal("タスクを完了にしました", vm.StatusMessage);
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday, "タスクを完了にしました。次は 9/25(金)")]    // 9/20(日)始まりの週 9/20〜9/26 の最後の稼働日
    [InlineData(DayOfWeek.Thursday, "タスクを完了にしました。次は 9/30(水)")]  // 9/24(木)始まりの週 9/24〜9/30 の最後の稼働日
    public void 週の始まりの設定が稼働日基準の次の回に効く(DayOfWeek weekStart, string expected)
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(Repeating(due: D(2026, 9, 14), repeat: "X-KADO=WEEK-LAST-WORKDAY"));
        var (vm, editors) = CreateVm(test, weekStart);

        // 今日は 9/24。期限(9/14)の週はとうに過ぎているので、今日を含む週の最後の稼働日になる
        editors.OnTask = editor => { editor.IsDone = true; return true; };
        vm.SelectedDate = D(2026, 9, 14);
        vm.EditTaskCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        Assert.Equal(expected, vm.StatusMessage);
    }

    // ------------------------------------------------------------------
    // 表示
    // ------------------------------------------------------------------

    [Fact]
    public void 繰り返しのタスクにだけ題名の後ろに印が付く()
    {
        var repeating = new TaskListItemViewModel(Repeating(), null);
        var plain = new TaskListItemViewModel(Repeating(repeat: null), null);
        var broken = new TaskListItemViewModel(Repeating(repeat: "こわれた"), null);

        Assert.Equal(" ↻", repeating.RepeatMark);
        Assert.Equal(string.Empty, plain.RepeatMark);

        // 読めない指定は繰り返さないので印も付けない
        Assert.Equal(string.Empty, broken.RepeatMark);
    }

    [Fact]
    public void 月のチップにも印が付く()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(Repeating(due: Today));
        test.Workspace.AddTask(Repeating("t2", due: Today, repeat: null));
        var (vm, _) = CreateVm(test);

        var chips = vm.Month.Cells.Single(c => c.Date == Today).AllTasks;

        Assert.Equal(" ↻", chips.Single(c => c.Id == "t1").RepeatMark);
        Assert.Equal(string.Empty, chips.Single(c => c.Id == "t2").RepeatMark);
    }

    // ------------------------------------------------------------------
    // 設定（同期が読む週の始まり）
    // ------------------------------------------------------------------

    [Fact]
    public void 保存した週の始まりだけを読める()
    {
        using var test = TestWorkspace.Create();

        // 保存が無ければ既定（日曜）
        Assert.Equal(DayOfWeek.Sunday, AppSettings.ReadWeekStart(test.Workspace.Settings));

        new AppSettings(test.Workspace.Settings).WeekStart = DayOfWeek.Monday;

        Assert.Equal(DayOfWeek.Monday, AppSettings.ReadWeekStart(test.Workspace.Settings));
    }

    [Fact]
    public void 稼働日を組み立て直しても保持している稼働日は変わらない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        var before = ws.WorkingDays;

        var built = ws.BuildWorkingDays();

        Assert.Same(before, ws.WorkingDays);
        Assert.Equal(before.Count, built.Count);
    }
}
