using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 送れないカレンダー（読み取り専用・Google から外れた）。
/// <para>
/// 入れ先の既定・1行入力・編集画面の候補から外す。すでに入ってしまった送れない予定は、
/// 手元だけで削除できる（Google には何も送らない）。「Google から外れた」カレンダーは
/// 左パネルに印と説明を出し、中の予定は見られて、手元だけで削除できる。
/// </para>
/// </summary>
public class UnsendableCalendarTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    private static CalendarSource ReadOnly(string id = "cal-ro", int order = -10) => new()
    {
        Id = id, Summary = "共有カレンダー", SortOrder = order,
        GoogleRaw = $$"""{"id":"{{id}}","accessRole":"reader"}""",
        UpdatedAt = DateTimeOffset.Now,
    };

    private static CalendarSource Owned(string id = "cal-rw", int order = 3) => new()
    {
        Id = id, Summary = "自分のカレンダー", SortOrder = order,
        GoogleRaw = $$"""{"id":"{{id}}","accessRole":"owner"}""",
        UpdatedAt = DateTimeOffset.Now,
    };

    private static CalendarEvent Event(string id, string calendarId, string? googleId = null) => new()
    {
        Id = id, Title = "打ち合わせ", Date = Today,
        StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
        CalendarId = calendarId, GoogleEventId = googleId,
        GoogleCalendarId = googleId is null ? null : calendarId,
        GoogleRaw = googleId is null ? null : $$"""{"id":"{{googleId}}","summary":"打ち合わせ"}""",
    };

    private static (MainViewModel Vm, FakeEditorPresenter Editors) Create(TestWorkspace test)
    {
        var editors = new FakeEditorPresenter();
        return (new MainViewModel(test.Workspace, Today, editors: editors), editors);
    }

    // ------------------------------------------------------------------
    // 入れ先から外す
    // ------------------------------------------------------------------

    [Fact]
    public void 既定の入れ先に読み取り専用を選ばない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(ReadOnly());
        test.Workspace.Sources.Upsert(Owned());

        var (vm, _) = Create(test);

        // 設定が読み取り専用を指していても、先頭が読み取り専用でも、そこには入れない
        vm.SourceLists.DefaultCalendarId = "cal-ro";
        vm.SourceLists.Refresh();

        Assert.NotEqual("cal-ro", vm.SourceLists.DefaultCalendar!.Id);
        Assert.False(vm.SourceLists.DefaultCalendar.IsReadOnly);
    }

    [Fact]
    public void 一行入力は読み取り専用のカレンダーへ入れない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(ReadOnly());
        test.Workspace.Sources.Upsert(Owned());

        var (vm, _) = Create(test);
        vm.SourceLists.DefaultCalendarId = "cal-ro";
        vm.SourceLists.Refresh();

        vm.QuickText = "打合せ";
        vm.QuickCommand.Execute(null);

        var added = test.Workspace.Events.All().Single(e => e.Title == "打合せ");
        Assert.NotEqual("cal-ro", added.CalendarId);
    }

    [Fact]
    public void 入れ先を読み取り専用や外れたカレンダーに選び直させない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(ReadOnly());
        test.Workspace.Sources.Upsert(Owned("cal-detached", 4));
        test.Workspace.Sources.SetCalendarDetached("cal-detached", true);

        var (vm, _) = Create(test);
        var before = vm.SourceLists.DefaultCalendar!.Id;

        vm.SourceLists.SetDefaultCalendar(vm.SourceLists.Calendars.Single(c => c.Id == "cal-ro"));
        vm.SourceLists.SetDefaultCalendar(vm.SourceLists.Calendars.Single(c => c.Id == "cal-detached"));

        Assert.Equal(before, vm.SourceLists.DefaultCalendar!.Id);
    }

    [Fact]
    public void Googleから外れたカレンダーも入れ先に選ばない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(Owned("cal-detached", -5));
        test.Workspace.Sources.SetCalendarDetached("cal-detached", true);

        var (vm, _) = Create(test);
        vm.SourceLists.DefaultCalendarId = "cal-detached";
        vm.SourceLists.Refresh();

        Assert.NotEqual("cal-detached", vm.SourceLists.DefaultCalendar!.Id);
    }

    [Fact]
    public void 新しい予定の編集画面の候補から外れたカレンダーを除く()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(Owned("cal-detached", 4));
        test.Workspace.Sources.SetCalendarDetached("cal-detached", true);
        test.Workspace.Sources.Upsert(Owned("cal-rw", 5));

        var (vm, editors) = Create(test);
        editors.OnEvent = _ => false;
        vm.AddEventCommand.Execute(null);

        var ids = editors.LastEventEditor!.Calendars.Select(c => c.Id).ToArray();
        Assert.Contains("cal-rw", ids);
        Assert.DoesNotContain("cal-detached", ids);
    }

    [Fact]
    public void 予定の移し先の候補から外れたカレンダーを除く()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(Owned("cal-a", 1));
        test.Workspace.Sources.Upsert(Owned("cal-detached", 4));
        test.Workspace.Sources.SetCalendarDetached("cal-detached", true);
        test.Workspace.AddEvent(Event("e1", "cal-a", "g1"));

        var (vm, editors) = Create(test);
        editors.OnEvent = _ => false;
        vm.EditEventCommand.Execute(Assert.Single(vm.SelectedDay.Events));

        var ids = editors.LastEventEditor!.Calendars.Select(c => c.Id).ToArray();
        Assert.Contains("cal-a", ids);
        Assert.DoesNotContain("cal-detached", ids);
    }

    // ------------------------------------------------------------------
    // すでに入ってしまった送れない予定は、手元だけで削除できる
    // ------------------------------------------------------------------

    [Fact]
    public void 読み取り専用に入ってしまった未送信の予定は手元だけで削除できる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(ReadOnly());
        test.Workspace.AddEvent(Event("stuck", "cal-ro"));

        var (vm, _) = Create(test);
        vm.DeleteChipCommand.Execute(vm.Month.Cells.Single(c => c.Date == Today).Events.Single());

        Assert.Null(test.Workspace.Events.Find("stuck"));

        // Google に何も送らない（削除の記録を残さない）
        Assert.Equal(0, test.Workspace.Tombstones.Count());
    }

    [Fact]
    public void 手元だけの削除は元に戻せる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(ReadOnly());
        test.Workspace.AddEvent(Event("stuck", "cal-ro"));

        var (vm, _) = Create(test);
        vm.DeleteChipCommand.Execute(vm.Month.Cells.Single(c => c.Date == Today).Events.Single());
        test.Workspace.UndoLast();

        Assert.NotNull(test.Workspace.Events.Find("stuck"));
        Assert.Equal(0, test.Workspace.Tombstones.Count());
    }

    [Fact]
    public void 読み取り専用でGoogleから受け取った予定は今までどおり削除できない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(ReadOnly());
        test.Workspace.AddEvent(Event("shared", "cal-ro", "g1"));

        var (vm, _) = Create(test);
        vm.DeleteChipCommand.Execute(vm.Month.Cells.Single(c => c.Date == Today).Events.Single());

        Assert.NotNull(test.Workspace.Events.Find("shared"));
        Assert.NotNull(vm.StatusMessage);
    }

    [Fact]
    public void Googleから外れたカレンダーの予定は見られて手元だけで削除できる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(Owned("cal-detached"));
        test.Workspace.AddEvent(Event("inside", "cal-detached", "g1"));
        test.Workspace.Sources.SetCalendarDetached("cal-detached", true);

        var (vm, editors) = Create(test);

        // 見られる
        var chip = vm.Month.Cells.Single(c => c.Date == Today).Events.Single();

        // 変えられない（送れない）
        editors.OnEvent = _ => false;
        vm.EditChipCommand.Execute(chip);
        Assert.True(editors.LastEventEditor!.IsReadOnly);
        Assert.False(editors.LastEventEditor.CanSave);
        Assert.False(vm.MoveEventTo("inside", Today.AddDays(1)));
        Assert.NotNull(vm.StatusMessage);

        // 手元だけで消せる。Google には何も送らない
        vm.DeleteChipCommand.Execute(chip);
        Assert.Null(test.Workspace.Events.Find("inside"));
        Assert.Equal(0, test.Workspace.Tombstones.Count());
    }

    // ------------------------------------------------------------------
    // 左パネルの印と説明、カレンダーごとの削除
    // ------------------------------------------------------------------

    [Fact]
    public void 外れたカレンダーには印と説明が付く()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(Owned("cal-detached"));
        test.Workspace.Sources.SetCalendarDetached("cal-detached", true);
        test.Workspace.Sources.Upsert(Owned("cal-ok", 9));

        var (vm, _) = Create(test);

        var detached = vm.SourceLists.Calendars.Single(c => c.Id == "cal-detached");
        Assert.True(detached.IsDetached);
        Assert.True(detached.HasStatusNote);
        Assert.False(string.IsNullOrEmpty(detached.StatusMarkText));
        Assert.Contains("Google の一覧から外れ", detached.StatusNote, StringComparison.Ordinal);
        Assert.Contains("手元", detached.StatusNote, StringComparison.Ordinal);

        var normal = vm.SourceLists.Calendars.Single(c => c.Id == "cal-ok");
        Assert.False(normal.HasStatusNote);
    }

    [Fact]
    public void 外れたカレンダーはカレンダーごと手元から削除できる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(Owned("cal-detached"));
        test.Workspace.AddEvent(Event("inside", "cal-detached", "g1"));
        test.Workspace.Tombstones.Record(
            "deleted-earlier", TombstoneRepository.EventKind, "g2", DateTimeOffset.Now, "cal-detached");
        test.Workspace.Sources.SetCalendarDetached("cal-detached", true);

        var (vm, _) = Create(test);
        var item = vm.SourceLists.Calendars.Single(c => c.Id == "cal-detached");

        Assert.True(item.CanDelete);
        Assert.True(vm.DeleteSourceCommand.CanExecute(item));

        vm.DeleteSourceCommand.Execute(item);

        Assert.Null(test.Workspace.Sources.FindCalendar("cal-detached"));
        Assert.Null(test.Workspace.Events.Find("inside"));
        Assert.Equal(0, test.Workspace.Tombstones.Count());
    }

    [Fact]
    public void 外れていないGoogleのカレンダーは今までどおり消せない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(Owned("cal-rw"));

        var (vm, _) = Create(test);
        var item = vm.SourceLists.Calendars.Single(c => c.Id == "cal-rw");

        Assert.False(item.CanDelete);
        Assert.False(vm.DeleteSourceCommand.CanExecute(item));
    }

    // ------------------------------------------------------------------
    // タスクリスト
    // ------------------------------------------------------------------

    private static TaskListSource GoogleList(string id, int order) => new()
    {
        Id = id, Title = id, SortOrder = order,
        GoogleRaw = $$"""{"id":"{{id}}","title":"{{id}}"}""", UpdatedAt = DateTimeOffset.Now,
    };

    [Fact]
    public void 外れたタスクリストは入れ先と移し先の候補から外す()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(GoogleList("list-detached", -5));
        test.Workspace.Sources.SetTaskListDetached("list-detached", true);
        test.Workspace.Sources.Upsert(GoogleList("list-ok", 5));

        var (vm, editors) = Create(test);
        vm.SourceLists.DefaultTaskListId = "list-detached";
        vm.SourceLists.Refresh();

        Assert.NotEqual("list-detached", vm.SourceLists.DefaultTaskList!.Id);

        editors.OnTask = _ => false;
        vm.AddTaskCommand.Execute(null);

        var ids = editors.LastTaskEditor!.TaskLists.Select(t => t.Id).ToArray();
        Assert.Contains("list-ok", ids);
        Assert.DoesNotContain("list-detached", ids);

        // 左パネルの印
        var item = vm.SourceLists.TaskLists.Single(t => t.Id == "list-detached");
        Assert.True(item.IsDetached);
        Assert.True(item.HasStatusNote);
        Assert.True(item.CanDelete);
    }

    [Fact]
    public void 外れたタスクリストはタスクごと手元から削除できる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(GoogleList("list-detached", 5));
        test.Workspace.Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "残してあったタスク", TaskListId = "list-detached", UpdatedAt = DateTimeOffset.Now,
        });
        test.Workspace.Sources.SetTaskListDetached("list-detached", true);

        var (vm, _) = Create(test);
        var item = vm.SourceLists.TaskLists.Single(t => t.Id == "list-detached");

        vm.DeleteSourceCommand.Execute(item);

        Assert.Null(test.Workspace.Sources.FindTaskList("list-detached"));
        Assert.Null(test.Workspace.Tasks.Find("t1"));
    }
}
