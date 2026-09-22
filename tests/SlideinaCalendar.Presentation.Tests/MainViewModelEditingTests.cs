using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// Phase 3 の完了条件は「単独で予定とタスクの管理が完結する」こと。
/// 追加・変更・削除がすべて Undo を通ることまで見る。
/// </summary>
public class MainViewModelEditingTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static (MainViewModel Vm, FakeEditorPresenter Editors) Create(TestWorkspace test)
    {
        var editors = new FakeEditorPresenter();
        return (new MainViewModel(test.Workspace, today: D(2026, 9, 24), editors: editors), editors);
    }

    [Fact]
    public void 予定を追加すると選択日に出る()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnEvent = editor =>
        {
            editor.Title = "10月度 生産台数計画 レビュー";
            editor.StartTimeText = "09:00";
            editor.EndTimeText = "10:30";
            editor.Location = "第2会議室";
            return true;
        };

        vm.AddEventCommand.Execute(null);

        var added = Assert.Single(vm.SelectedDay.Events);
        Assert.Equal("10月度 生産台数計画 レビュー", added.Title);
        Assert.Equal("09:00", added.TimeText);
        Assert.Equal("第2会議室 ・ 1時間30分", added.SubText);

        // 月ビューにも出る
        Assert.Single(vm.Month.Cells.Single(c => c.Date == D(2026, 9, 24)).AllEvents);
    }

    /// <summary>
    /// 時間軸のマスをダブルクリックしたときの追加（項目2）。
    /// <para>
    /// 実際の位置→時刻の変換は <c>TimelineColumnView.TimeAt</c>（WPF、Linux では
    /// 検査できない）が行い、丸めた時刻を <see cref="MainViewModel.AddEventAt"/> に渡す。
    /// ここでは渡された時刻がそのまま開始になり、長さは既定の1時間を保つことを見る。
    /// </para>
    /// </summary>
    [Fact]
    public void 時間軸をダブルクリックした時刻が開始になる()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnEvent = editor =>
        {
            editor.Title = "打合せ";
            return true;
        };

        vm.AddEventAt(D(2026, 9, 14), new TimeOnly(14, 15));

        // 選んでいる日もその日へ移る
        Assert.Equal(D(2026, 9, 14), vm.SelectedDate);
        Assert.Equal("14:15", editors.LastEventEditor!.StartTimeText);

        // 開始を動かすと終了も同じ長さ（既定の1時間）のまま付いてくる
        Assert.Equal("15:15", editors.LastEventEditor!.EndTimeText);

        var added = Assert.Single(vm.SelectedDay.Events);
        Assert.Equal("14:15", added.TimeText);
    }

    [Fact]
    public void 追加の初期値は選択している日()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);
        vm.SelectedDate = D(2026, 9, 14);

        editors.OnEvent = _ => false;
        vm.AddEventCommand.Execute(null);

        Assert.Equal(D(2026, 9, 14), editors.LastEventEditor!.Date);
    }

    [Fact]
    public void 取り消すと何も起きない()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnEvent = editor => { editor.Title = "書きかけ"; return false; };
        vm.AddEventCommand.Execute(null);

        Assert.Empty(vm.SelectedDay.Events);
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public void 予定の追加は元に戻せる()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnEvent = editor => { editor.Title = "会議"; return true; };
        vm.AddEventCommand.Execute(null);

        Assert.True(vm.CanUndo);
        vm.UndoCommand.Execute(null);

        Assert.Empty(vm.SelectedDay.Events);
        Assert.Contains("元に戻しました", vm.StatusMessage);

        vm.RedoCommand.Execute(null);
        Assert.Single(vm.SelectedDay.Events);
    }

    [Fact]
    public void 予定を変更できる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24), StartTime = new TimeOnly(10, 0),
            EndTime = new TimeOnly(11, 0),
        });

        var (vm, editors) = Create(test);
        editors.OnEvent = editor => { editor.Title = "定例（時間変更）"; editor.StartTimeText = "13:00";
                                      editor.EndTimeText = "14:00"; return true; };

        vm.EditEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        var after = Assert.Single(vm.SelectedDay.Events);
        Assert.Equal("定例（時間変更）", after.Title);
        Assert.Equal("13:00", after.TimeText);
    }

    [Fact]
    public void 予定を削除できる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });

        var (vm, _) = Create(test);
        vm.DeleteEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        Assert.Empty(vm.SelectedDay.Events);
    }

    /// <summary>
    /// 削除に確認ダイアログは出さない（Undo が効くので不要）。
    /// 手応えは Undo で戻せることで担保する。
    /// </summary>
    [Fact]
    public void 削除は確認なしですぐ消え_Undoで戻せる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });

        var (vm, _) = Create(test);
        vm.DeleteEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        Assert.Empty(vm.SelectedDay.Events);

        vm.UndoCommand.Execute(null);
        Assert.Single(vm.SelectedDay.Events);
    }

    [Fact]
    public void タスクを追加して完了を切り替えられる()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnTask = editor => { editor.Title = "台数計画の確定"; return true; };
        vm.AddTaskCommand.Execute(null);

        var task = Assert.Single(vm.SelectedDay.Tasks);
        Assert.False(task.IsDone);
        // 見出しは「残り / 全体」
        Assert.Equal("1 / 1", vm.SelectedDay.TaskCountText);

        vm.ToggleTaskDoneCommand.Execute(task);

        Assert.True(Assert.Single(vm.SelectedDay.Tasks).IsDone);
        Assert.Equal("0 / 1", vm.SelectedDay.TaskCountText);

        // 切り替えも元に戻せる
        vm.UndoCommand.Execute(null);
        Assert.False(Assert.Single(vm.SelectedDay.Tasks).IsDone);
    }

    [Fact]
    public void タスクを削除できる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "提出", Due = D(2026, 9, 24) });

        var (vm, _) = Create(test);
        vm.DeleteTaskCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        Assert.Empty(vm.SelectedDay.Tasks);

        vm.UndoCommand.Execute(null);
        Assert.Single(vm.SelectedDay.Tasks);
    }

    // ------------------------------------------------------------------
    // タスクの並べ替え
    //
    // 同じ期限日（期限なしなら期限なしどうし）の中だけで入れ替えられる。
    // 期限日をまたぐ移動は認めない
    // ------------------------------------------------------------------

    [Fact]
    public void 右クリックメニューで上へ下へ動かせる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(new TaskItem { Id = "t1", Title = "A", Due = D(2026, 9, 24) });
        ws.AddTask(new TaskItem { Id = "t2", Title = "B", Due = D(2026, 9, 24) });
        ws.AddTask(new TaskItem { Id = "t3", Title = "C", Due = D(2026, 9, 24) });

        var (vm, _) = Create(test);
        Assert.Equal(["A", "B", "C"], vm.SelectedDay.Tasks.Select(t => t.Title));

        var b = vm.SelectedDay.Tasks.Single(t => t.Id == "t2");
        vm.MoveTaskUpCommand.Execute(b);

        Assert.Equal(["B", "A", "C"], vm.SelectedDay.Tasks.Select(t => t.Title));

        var a = vm.SelectedDay.Tasks.Single(t => t.Id == "t1");
        vm.MoveTaskDownCommand.Execute(a);

        Assert.Equal(["B", "C", "A"], vm.SelectedDay.Tasks.Select(t => t.Title));

        // SortOrder はそのグループの中で 0 から振り直されている
        Assert.Equal(
            [0, 1, 2],
            new[] { "t2", "t3", "t1" }.Select(id => test.Workspace.Tasks.Find(id)!.SortOrder));
    }

    [Fact]
    public void グループの端では上へ下へが無効になる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(new TaskItem { Id = "t1", Title = "先頭", Due = D(2026, 9, 24) });
        ws.AddTask(new TaskItem { Id = "t2", Title = "末尾", Due = D(2026, 9, 24) });

        var (vm, _) = Create(test);
        var first = vm.SelectedDay.Tasks.Single(t => t.Id == "t1");
        var last = vm.SelectedDay.Tasks.Single(t => t.Id == "t2");

        Assert.False(vm.MoveTaskUpCommand.CanExecute(first));
        Assert.True(vm.MoveTaskDownCommand.CanExecute(first));
        Assert.True(vm.MoveTaskUpCommand.CanExecute(last));
        Assert.False(vm.MoveTaskDownCommand.CanExecute(last));

        // 実行しても動かない（先頭のまま）
        vm.MoveTaskUpCommand.Execute(first);
        Assert.Equal(["先頭", "末尾"], vm.SelectedDay.Tasks.Select(t => t.Title));
    }

    [Fact]
    public void ドラッグで同じ期限日の中を入れ替えられる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(new TaskItem { Id = "t1", Title = "A", Due = D(2026, 9, 24) });
        ws.AddTask(new TaskItem { Id = "t2", Title = "B", Due = D(2026, 9, 24) });
        ws.AddTask(new TaskItem { Id = "t3", Title = "C", Due = D(2026, 9, 24) });

        var (vm, _) = Create(test);
        var a = vm.SelectedDay.Tasks.Single(t => t.Id == "t1");
        var c = vm.SelectedDay.Tasks.Single(t => t.Id == "t3");

        // A を C の下へ落とす
        var moved = vm.MoveTask(a, c, above: false);

        Assert.True(moved);
        Assert.Equal(["B", "C", "A"], vm.SelectedDay.Tasks.Select(t => t.Title));
    }

    [Fact]
    public void 期限日をまたぐ移動はできない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(new TaskItem { Id = "t1", Title = "今日", Due = D(2026, 9, 24) });
        ws.AddTask(new TaskItem { Id = "t2", Title = "来月", Due = D(2026, 10, 1) });

        var (vm, _) = Create(test);
        var today = vm.SelectedDay.Tasks.Single(t => t.Id == "t1");
        var nextMonth = vm.SelectedDay.Tasks.Single(t => t.Id == "t2");

        Assert.False(vm.CanMoveTask(today, nextMonth));
        Assert.False(vm.MoveTask(today, nextMonth, above: true));

        // 期限日は変わっていない
        Assert.Equal(D(2026, 9, 24), test.Workspace.Tasks.Find("t1")!.Due);
        Assert.Equal(D(2026, 10, 1), test.Workspace.Tasks.Find("t2")!.Due);
    }

    [Fact]
    public void 期限なしタスクどうしも並べ替えられる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;
        ws.AddTask(new TaskItem { Id = "t1", Title = "先に登録" });
        ws.AddTask(new TaskItem { Id = "t2", Title = "あとから登録" });

        var (vm, _) = Create(test);
        var first = vm.SelectedDay.NoDueTasks.Single(t => t.Id == "t1");

        vm.MoveTaskDownCommand.Execute(first);

        Assert.Equal(["あとから登録", "先に登録"], vm.SelectedDay.NoDueTasks.Select(t => t.Title));
    }

    [Fact]
    public void 編集画面には保存されている内容を渡す()
    {
        using var test = TestWorkspace.Create();

        // 表示用に展開された複製ではなく、保存されている姿を直す
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "週次定例", Date = D(2026, 9, 7),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(9, 30),
            Recurrence = "FREQ=WEEKLY;BYDAY=MO",
        });

        var (vm, editors) = Create(test);
        vm.SelectedDate = D(2026, 9, 21);   // 展開された回

        editors.OnEvent = _ => false;
        vm.EditEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        // 開始日は繰り返しの元の日であって、表示している日ではない
        Assert.Equal(D(2026, 9, 7), editors.LastEventEditor!.Date);
    }

    [Fact]
    public void 編集画面の候補には左パネルの一覧を渡す()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "会議", Date = D(2026, 9, 24), CalendarId = "仕事",
        });

        test.Workspace.EnsureSources();

        var (vm, editors) = Create(test);
        editors.OnEvent = _ => false;
        vm.AddEventCommand.Execute(null);

        // 既定の入れ先に加えて、予定が指している所属も候補に出る
        Assert.Contains(editors.LastEventEditor!.Calendars, c => c.Name == "仕事");
    }

    // ------------------------------------------------------------------
    // 月ビューのマスに並ぶ予定・タスク
    //
    // 実機で、マスの予定をダブルクリックしても編集画面にならず、右クリックの
    // メニューも無かった。右ペインの行とは別の型なので、別の入口が要る
    // ------------------------------------------------------------------

    private static EventChipViewModel Chip(MainViewModel vm, string id) =>
        vm.Month.Cells.SelectMany(c => c.Events).First(e => e.Id == id);

    [Fact]
    public void マスの予定を開いて直せる()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        });

        editors.OnEvent = editor => { editor.Title = "定例（変更）"; return true; };
        vm.EditChipCommand.Execute(Chip(vm, "e1"));

        Assert.Equal("定例（変更）", test.Workspace.Events.Find("e1")!.Title);
    }

    [Fact]
    public void マスの予定を消せる()
    {
        using var test = TestWorkspace.Create();
        var (vm, _) = Create(test);

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        });

        var chip = Chip(vm, "e1");
        vm.DeleteChipCommand.Execute(chip);

        // 確認なしですぐ消える。手応えは Undo で戻せることで返す
        Assert.Null(test.Workspace.Events.Find("e1"));

        vm.UndoCommand.Execute(null);
        Assert.NotNull(test.Workspace.Events.Find("e1"));
    }

    [Fact]
    public void マスのタスクを開いて直せる()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "提出", Due = D(2026, 9, 24) });

        var task = vm.Month.Cells.SelectMany(c => c.Tasks).First(t => t.Id == "t1");

        editors.OnTask = editor => { editor.Title = "提出（変更）"; return true; };
        vm.EditTaskChipCommand.Execute(task);

        Assert.Equal("提出（変更）", test.Workspace.Tasks.Find("t1")!.Title);
    }

    [Fact]
    public void マスのタスクを消せる()
    {
        using var test = TestWorkspace.Create();
        var (vm, _) = Create(test);

        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "提出", Due = D(2026, 9, 24) });

        var task = vm.Month.Cells.SelectMany(c => c.Tasks).First(t => t.Id == "t1");
        vm.DeleteTaskChipCommand.Execute(task);

        Assert.Null(test.Workspace.Tasks.Find("t1"));
    }

    [Fact]
    public void すでに無いものを開こうとしても落ちない()
    {
        using var test = TestWorkspace.Create();
        var (vm, _) = Create(test);

        vm.EditChipCommand.Execute(null);
        vm.DeleteChipCommand.Execute(null);
        vm.EditTaskChipCommand.Execute(null);
        vm.DeleteTaskChipCommand.Execute(null);
    }

    // ------------------------------------------------------------------
    // 週ビュー・日ビューの時間軸
    // ------------------------------------------------------------------

    private static TimeBlockViewModel Block(MainViewModel vm, string id) =>
        vm.Week.Days.SelectMany(d => d.Blocks).First(b => b.Id == id);

    [Fact]
    public void 時間軸の予定を開いて直せる()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        });

        editors.OnEvent = editor => { editor.Title = "定例（変更）"; return true; };
        vm.EditBlockCommand.Execute(Block(vm, "e1"));

        Assert.Equal("定例（変更）", test.Workspace.Events.Find("e1")!.Title);
    }

    [Fact]
    public void 時間軸の予定を消せる()
    {
        using var test = TestWorkspace.Create();
        var (vm, _) = Create(test);

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        });

        vm.DeleteBlockCommand.Execute(Block(vm, "e1"));

        Assert.Null(test.Workspace.Events.Find("e1"));
    }

    [Fact]
    public void 作業時間ブロックはもとのタスクが開く()
    {
        using var test = TestWorkspace.Create();

        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "提出", Due = D(2026, 9, 24) });
        test.Workspace.Tasks.UpsertBlock(new WorkBlock
        {
            Id = "b1", TaskId = "t1", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(13, 0), DurationMinutes = 60,
        });

        var (vm, editors) = Create(test);
        var block = Block(vm, "b1");
        Assert.True(block.IsWorkBlock);

        // ブロック自身の識別子で予定を探しても見つからない
        editors.OnTask = editor => { editor.Title = "提出（変更）"; return true; };
        vm.EditBlockCommand.Execute(block);

        Assert.Equal("提出（変更）", test.Workspace.Tasks.Find("t1")!.Title);
    }

    [Fact]
    public void 作業時間ブロックは消せない()
    {
        using var test = TestWorkspace.Create();

        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "提出", Due = D(2026, 9, 24) });
        test.Workspace.Tasks.UpsertBlock(new WorkBlock
        {
            Id = "b1", TaskId = "t1", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(13, 0), DurationMinutes = 60,
        });

        var (vm, _) = Create(test);

        // 予定ではないので、予定の削除は動かない。タスクも消えない
        vm.DeleteBlockCommand.Execute(Block(vm, "b1"));

        Assert.NotNull(test.Workspace.Tasks.Find("t1"));
    }

    // ------------------------------------------------------------------
    // 項目6: エディタの「削除」ボタンを受ける配線
    // ------------------------------------------------------------------

    [Fact]
    public void 予定エディタの削除ボタンで削除される()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });

        var (vm, editors) = Create(test);

        // 編集画面の「削除」は RequestDelete() を呼んだあと DialogResult=false で閉じる
        editors.OnEvent = editor => { editor.RequestDelete(); return false; };

        vm.EditChipCommand.Execute(Chip(vm, "e1"));

        Assert.Null(test.Workspace.Events.Find("e1"));
    }

    [Fact]
    public void 予定エディタを取り消しで閉じても削除しない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });

        var (vm, editors) = Create(test);

        // 削除は押していない。ふつうの取り消しなら消さない
        editors.OnEvent = _ => false;

        vm.EditChipCommand.Execute(Chip(vm, "e1"));

        Assert.NotNull(test.Workspace.Events.Find("e1"));
    }

    [Fact]
    public void タスクエディタの削除ボタンで削除される()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "提出", Due = D(2026, 9, 24) });

        var (vm, editors) = Create(test);
        editors.OnTask = editor => { editor.RequestDelete(); return false; };

        vm.EditTaskCommand.Execute(Assert.Single(vm.SelectedDay.Tasks));

        Assert.Null(test.Workspace.Tasks.Find("t1"));
    }

    [Fact]
    public void 右ペインのタスクエディタの削除ボタンでも削除される()
    {
        // EditTask（右ペイン用）は EditTaskBy とは別の実装なので、こちらも確かめる
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "提出", Due = D(2026, 9, 24) });

        var (vm, editors) = Create(test);
        editors.OnTask = editor => { editor.RequestDelete(); return false; };

        var task = vm.Month.Cells.SelectMany(c => c.Tasks).First(t => t.Id == "t1");
        vm.EditTaskChipCommand.Execute(task);

        Assert.Null(test.Workspace.Tasks.Find("t1"));
    }

    // ------------------------------------------------------------------
    // 項目1: ステータス表示
    // ------------------------------------------------------------------

    [Fact]
    public void 操作の結果がStatusMessageに出て消せる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });

        var (vm, _) = Create(test);
        vm.DeleteEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        Assert.Equal("予定を削除しました", vm.StatusMessage);

        // 画面側が一定時間後に呼ぶ。すでに出ている内容を消せる
        vm.ClearStatusMessage();
        Assert.Null(vm.StatusMessage);
    }

    // ------------------------------------------------------------------
    // 項目7: 実働日計算パネルの配線（モードレス化・日付クリックの受け）
    // ------------------------------------------------------------------

    [Fact]
    public void 実働日計算を開くまでは閉じている()
    {
        using var test = TestWorkspace.Create();
        var (vm, _) = Create(test);

        Assert.False(vm.IsWorkdayCalculatorOpen);
    }

    [Fact]
    public void 開いているあいだだけ日付クリックを流せる()
    {
        using var test = TestWorkspace.Create();
        var (vm, _) = Create(test);

        // 開く前に流しても、当てる先が無いので何も起きない
        vm.FeedWorkdayCalculator(D(2026, 10, 1), isEnd: false);

        vm.OpenWorkingDayCalculatorCommand.Execute(null);
        Assert.True(vm.IsWorkdayCalculatorOpen);

        vm.FeedWorkdayCalculator(D(2026, 10, 1), isEnd: false);
        vm.FeedWorkdayCalculator(D(2026, 10, 10), isEnd: true);

        // FakeEditorPresenter は ShowWorkdayCalculator を記録するだけなので、
        // ここでは「例外なく呼べる」ことまでを確かめる（実際の反映は WorkdayCalculatorViewModel 側でテスト済み）
    }

    [Fact]
    public void 二重に開いても同じパネルを使い回す()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        vm.OpenWorkingDayCalculatorCommand.Execute(null);
        var first = editors.LastCalculator;

        vm.OpenWorkingDayCalculatorCommand.Execute(null);
        var second = editors.LastCalculator;

        // 作り直していれば別インスタンスになり、入力中の内容が消えてしまう
        Assert.Same(first, second);
    }
}
