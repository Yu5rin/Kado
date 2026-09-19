using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 左パネルのカレンダーとタスクリストの作成・変更・削除。
/// <para>
/// Google に繋がなくても、仕事と私用を分けて入れられることが要る。
/// </para>
/// </summary>
public class MainViewModelSourceTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static (MainViewModel Vm, FakeEditorPresenter Editors) Create(TestWorkspace test)
    {
        var editors = new FakeEditorPresenter();
        return (new MainViewModel(test.Workspace, today: D(2026, 9, 24), editors: editors), editors);
    }

    [Fact]
    public void 起動しただけで既定のカレンダーとタスクリストがある()
    {
        using var test = TestWorkspace.Create();
        var (vm, _) = Create(test);

        // 1つも無いと予定の入れ先が決まらない
        Assert.Equal(CalendarWorkspace.DefaultCalendarName, Assert.Single(vm.SourceLists.Calendars).Name);
        Assert.Equal(CalendarWorkspace.DefaultTaskListName, Assert.Single(vm.SourceLists.TaskLists).Name);
    }

    [Fact]
    public void カレンダーを作ると左パネルに出る()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnCalendar = editor =>
        {
            editor.Name = "仕事";
            return true;
        };

        vm.AddCalendarCommand.Execute(null);

        Assert.Contains(vm.SourceLists.Calendars, c => c.Name == "仕事");
        Assert.Contains("仕事", vm.StatusMessage);
    }

    [Fact]
    public void 作ったカレンダーの色は既存と重ならない()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnCalendar = editor =>
        {
            editor.Name = "私用";
            return true;
        };

        vm.AddCalendarCommand.Execute(null);

        var colors = vm.SourceLists.Calendars.Select(c => c.SwatchColor).ToArray();
        Assert.Equal(colors.Length, colors.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void 取り消すと作られない()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnCalendar = _ => false;
        vm.AddCalendarCommand.Execute(null);

        Assert.Single(vm.SourceLists.Calendars);
    }

    [Fact]
    public void タスクリストを作れる()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnCalendar = editor =>
        {
            Assert.True(editor.IsTaskList);

            // タスクリストは色を持たない。色欄を出さない
            Assert.False(editor.HasColor);

            editor.Name = "買い物";
            return true;
        };

        vm.AddTaskListCommand.Execute(null);

        Assert.Contains(vm.SourceLists.TaskLists, t => t.Name == "買い物");
    }

    [Fact]
    public void 名前と色を変えられる()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnCalendar = editor =>
        {
            editor.Name = "仕事";
            editor.Color = "#c0392b";
            return true;
        };

        vm.EditSourceCommand.Execute(vm.SourceLists.Calendars[0]);

        var changed = Assert.Single(vm.SourceLists.Calendars);
        Assert.Equal("仕事", changed.Name);
        Assert.Equal("#c0392b", changed.SwatchColor);
    }

    [Fact]
    public void 予定の色は所属カレンダーの色になる()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        var calendar = vm.SourceLists.Calendars[0];
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24), CalendarId = calendar.Id,
        });

        editors.OnCalendar = editor =>
        {
            editor.Name = calendar.Name;
            editor.Color = "#8e44ad";
            return true;
        };

        vm.EditSourceCommand.Execute(vm.SourceLists.Calendars[0]);

        // 1件ずつ色を選ばせない。カレンダーを塗り替えれば中の予定もついてくる
        Assert.Equal("#8e44ad", vm.SourceLists.ColorOf(calendar.Id));
    }

    [Fact]
    public void カレンダーを消しても中の予定は消えず別のカレンダーへ移る()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnCalendar = editor => { editor.Name = "私用"; return true; };
        vm.AddCalendarCommand.Execute(null);

        var target = vm.SourceLists.Calendars.Single(c => c.Name == "私用");
        var other = vm.SourceLists.Calendars.Single(c => c.Name != "私用");

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "歯医者", Date = D(2026, 9, 24), CalendarId = target.Id,
        });

        vm.DeleteSourceCommand.Execute(vm.SourceLists.Calendars.Single(c => c.Id == target.Id));

        Assert.DoesNotContain(vm.SourceLists.Calendars, c => c.Id == target.Id);

        // 分類を消したかっただけで、中身まで消えるのは行き過ぎ
        var moved = Assert.Single(test.Workspace.Events.All());
        Assert.Equal("歯医者", moved.Title);
        Assert.Equal(other.Id, moved.CalendarId);
    }

    [Fact]
    public void 削除の確認では移る件数を伝える()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnCalendar = editor => { editor.Name = "私用"; return true; };
        vm.AddCalendarCommand.Execute(null);

        var target = vm.SourceLists.Calendars.Single(c => c.Name == "私用");
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "歯医者", Date = D(2026, 9, 24), CalendarId = target.Id,
        });

        editors.Confirms = false;
        vm.DeleteSourceCommand.Execute(vm.SourceLists.Calendars.Single(c => c.Id == target.Id));

        Assert.Contains("1 件", editors.LastConfirmMessage);

        // 断ったので消えていない
        Assert.Contains(vm.SourceLists.Calendars, c => c.Id == target.Id);
    }

    [Fact]
    public void 最後のカレンダーは消せない()
    {
        using var test = TestWorkspace.Create();
        var (vm, _) = Create(test);

        // 入れ先が無くなると予定の分類ができなくなる
        vm.DeleteSourceCommand.Execute(vm.SourceLists.Calendars[0]);

        Assert.Single(vm.SourceLists.Calendars);
        Assert.Equal("最後のカレンダーは削除できません", vm.StatusMessage);
    }

    [Fact]
    public void 最後のタスクリストも消せない()
    {
        using var test = TestWorkspace.Create();
        var (vm, _) = Create(test);

        vm.DeleteSourceCommand.Execute(vm.SourceLists.TaskLists[0]);

        Assert.Single(vm.SourceLists.TaskLists);
        Assert.Equal("最後のタスクリストは削除できません", vm.StatusMessage);
    }

    [Fact]
    public void 作ったカレンダーは予定の入力欄に名前で出る()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnCalendar = editor => { editor.Name = "仕事"; return true; };
        vm.AddCalendarCommand.Execute(null);

        editors.OnEvent = _ => false;
        vm.AddEventCommand.Execute(null);

        // ID は local:… という機械的な文字列。そのまま並べると画面に出てしまう
        var choice = Assert.Single(editors.LastEventEditor!.Calendars, c => c.Name == "仕事");
        Assert.NotEqual(choice.Id, choice.Name);
        Assert.Equal("仕事", choice.ToString());
    }
}
