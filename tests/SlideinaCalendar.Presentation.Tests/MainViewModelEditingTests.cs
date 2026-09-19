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

        var (vm, editors) = Create(test);
        vm.DeleteEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        Assert.Empty(vm.SelectedDay.Events);
        Assert.Equal("会議", editors.LastConfirmedTitle);
    }

    [Fact]
    public void 確認で断ると削除しない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });

        var (vm, editors) = Create(test);
        editors.ConfirmsDelete = false;

        vm.DeleteEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

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
        Assert.Equal("0 / 1", vm.SelectedDay.TaskCountText);

        vm.ToggleTaskDoneCommand.Execute(task);

        Assert.True(Assert.Single(vm.SelectedDay.Tasks).IsDone);
        Assert.Equal("1 / 1", vm.SelectedDay.TaskCountText);

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

        var (vm, editors) = Create(test);
        editors.OnEvent = _ => false;
        vm.AddEventCommand.Execute(null);

        Assert.Equal(["仕事"], editors.LastEventEditor!.Calendars);
    }
}
