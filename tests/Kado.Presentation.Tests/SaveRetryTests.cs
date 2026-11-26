using Dapper;
using Kado.Data.Models;
using Kado.Presentation.Editing;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 書き込みに失敗したとき、編集画面を同じ入力のまま出し直す。
/// <para>
/// 失敗のたびに画面を閉じると、打った内容が全部消える。失敗は表に仕掛けた
/// トリガー（<c>RAISE(ABORT)</c>）で作り、画面が開いたところで外す。
/// </para>
/// </summary>
public class SaveRetryTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    private static (MainViewModel Main, FakeEditorPresenter Editors) Create(TestWorkspace test)
    {
        var editors = new FakeEditorPresenter();
        var main = new MainViewModel(test.Workspace, Today, editors: editors, files: new FakeFileDialogs());
        return (main, editors);
    }

    private static void FailWrites(TestWorkspace test, string table, string kind)
    {
        test.Connection.Execute(
            $"CREATE TRIGGER fail_{table}_{kind} BEFORE {kind} ON {table} BEGIN SELECT RAISE(ABORT, 'disk full'); END;");
    }

    private static void Heal(TestWorkspace test, string table, string kind) =>
        test.Connection.Execute($"DROP TRIGGER fail_{table}_{kind}");

    [Fact]
    public void 新しい予定が書けなければ同じ入力で画面を出し直す()
    {
        using var test = TestWorkspace.Create();
        var (main, editors) = Create(test);
        FailWrites(test, "events", "INSERT");

        var shown = new List<EventEditorViewModel>();
        editors.OnEvent = editor =>
        {
            shown.Add(editor);

            if (shown.Count == 1) editor.Title = "打ち合わせ";
            else Heal(test, "events", "INSERT");   // 2回目に開いたときは書ける

            return true;
        };

        main.AddEventCommand.Execute(null);

        Assert.Equal(2, shown.Count);
        Assert.Same(shown[0], shown[1]);
        Assert.Equal("打ち合わせ", shown[1].Title);
        Assert.Equal("打ち合わせ", Assert.Single(test.Workspace.Events.All()).Title);
    }

    [Fact]
    public void 書けない予定の画面を取り消せば抜けられる()
    {
        using var test = TestWorkspace.Create();
        var (main, editors) = Create(test);
        FailWrites(test, "events", "INSERT");

        var opened = 0;
        editors.OnEvent = editor =>
        {
            editor.Title = "打ち合わせ";
            return ++opened == 1;   // 2回目は取り消す
        };

        main.AddEventCommand.Execute(null);

        Assert.Equal(2, opened);
        Assert.Empty(test.Workspace.Events.All());
    }

    [Fact]
    public void 新しいタスクが書けなければ同じ入力で画面を出し直す()
    {
        using var test = TestWorkspace.Create();
        var (main, editors) = Create(test);
        FailWrites(test, "tasks", "INSERT");

        var shown = new List<TaskEditorViewModel>();
        editors.OnTask = editor =>
        {
            shown.Add(editor);

            if (shown.Count == 1) editor.Title = "資料を作る";
            else Heal(test, "tasks", "INSERT");

            return true;
        };

        main.AddTaskCommand.Execute(null);

        Assert.Equal(2, shown.Count);
        Assert.Same(shown[0], shown[1]);
        Assert.Equal("資料を作る", Assert.Single(test.Workspace.Tasks.All()).Title);
    }

    [Fact]
    public void 既存の予定の変更が書けなければ同じ入力で画面を出し直す()
    {
        using var test = TestWorkspace.Create();
        var (main, editors) = Create(test);
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = Today });
        FailWrites(test, "events", "UPDATE");

        var shown = new List<EventEditorViewModel>();
        editors.OnEvent = editor =>
        {
            shown.Add(editor);

            if (shown.Count == 1) editor.Title = "会議（変更）";
            else Heal(test, "events", "UPDATE");

            return true;
        };

        main.EditChipCommand.Execute(main.Month.Cells.Single(c => c.Date == Today).Events.Single());

        Assert.Equal(2, shown.Count);
        Assert.Equal("会議（変更）", shown[1].Title);
        Assert.Equal("会議（変更）", test.Workspace.Events.Find("e1")!.Title);
    }

    [Fact]
    public void 既存のタスクの変更が書けなければ同じ入力で画面を出し直す()
    {
        using var test = TestWorkspace.Create();
        var (main, editors) = Create(test);
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "資料", Due = Today });
        FailWrites(test, "tasks", "UPDATE");

        var shown = new List<TaskEditorViewModel>();
        editors.OnTask = editor =>
        {
            shown.Add(editor);

            if (shown.Count == 1) editor.Title = "資料（変更）";
            else Heal(test, "tasks", "UPDATE");

            return true;
        };

        main.EditTaskChipCommand.Execute(main.Month.Cells.Single(c => c.Date == Today).Tasks.Single());

        Assert.Equal(2, shown.Count);
        Assert.Equal("資料（変更）", test.Workspace.Tasks.Find("t1")!.Title);
    }
}
