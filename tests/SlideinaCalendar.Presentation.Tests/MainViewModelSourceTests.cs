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

    // ------------------------------------------------------------------
    // Google のものは消させない
    //
    // 手元から消しても次の同期で一覧から戻ってくる。そのうえ中の予定は別の
    // カレンダーへ移されたまま取り残される。消えたように見えて消えていない
    // ------------------------------------------------------------------

    /// <summary>Google から取り込んだ体のカレンダーを1件足す。</summary>
    private static void AddGoogleCalendar(TestWorkspace test, string id, string name) =>
        test.Workspace.Sources.Upsert(new CalendarSource
        {
            Id = id,
            Summary = name,
            GoogleRaw = $$"""{"id":"{{id}}","summary":"{{name}}","accessRole":"owner"}""",
            UpdatedAt = DateTimeOffset.Now,
        });

    [Fact]
    public void Google_のカレンダーは消せない()
    {
        using var test = TestWorkspace.Create();
        AddGoogleCalendar(test, "shigoto@group.calendar.google.com", "仕事");

        var (vm, _) = Create(test);
        var target = vm.SourceLists.GoogleCalendars.Single(c => c.Name == "仕事");

        // 画面ではメニューに出さない。押せてしまっても動かないようにしておく
        Assert.False(vm.DeleteSourceCommand.CanExecute(target));

        vm.DeleteSourceCommand.Execute(target);

        Assert.Contains(vm.SourceLists.Calendars, c => c.Id == target.Id);
    }

    [Fact]
    public void このアプリのカレンダーは今までどおり消せる()
    {
        using var test = TestWorkspace.Create();
        AddGoogleCalendar(test, "shigoto@group.calendar.google.com", "仕事");

        var (vm, editors) = Create(test);

        editors.OnCalendar = editor => { editor.Name = "私用"; return true; };
        vm.AddCalendarCommand.Execute(null);

        var target = vm.SourceLists.LocalCalendars.Single(c => c.Name == "私用");

        Assert.True(vm.DeleteSourceCommand.CanExecute(target));

        vm.DeleteSourceCommand.Execute(target);

        Assert.DoesNotContain(vm.SourceLists.Calendars, c => c.Id == target.Id);
    }

    [Fact]
    public void カレンダーの色を変えると予定の帯もすぐ変わる()
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

        const string wanted = "#8d6e63";
        editors.OnCalendar = editor => { editor.Color = wanted; return true; };
        vm.EditSourceCommand.Execute(vm.SourceLists.Calendars.Single(c => c.Id == target.Id));

        // 左パネルの色見本
        Assert.Equal(wanted, vm.SourceLists.Calendars.Single(c => c.Id == target.Id).SwatchColor);

        // 月ビューの帯。ここが古い色のままだと「色を変えても反映されない」ことになる
        var chip = vm.Month.Cells
            .SelectMany(c => c.Events)
            .Single(e => e.Label.Contains("歯医者", StringComparison.Ordinal));

        Assert.Equal(wanted, chip.Color);

        // 右ペインも同じ色を引く
        Assert.Equal(wanted, vm.SelectedDay.Events.Single(e => e.Id == "e1").Color);
    }

    [Fact]
    public void 消したカレンダーの予定は_Google_へ移さない()
    {
        using var test = TestWorkspace.Create();

        // 残っているものの先頭に入れると、手元の予定が次の同期で勝手に相手へ送られる
        AddGoogleCalendar(test, "aaa@group.calendar.google.com", "仕事");

        var (vm, editors) = Create(test);

        editors.OnCalendar = editor => { editor.Name = "私用"; return true; };
        vm.AddCalendarCommand.Execute(null);

        var target = vm.SourceLists.LocalCalendars.Single(c => c.Name == "私用");

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "歯医者", Date = D(2026, 9, 24), CalendarId = target.Id,
        });

        vm.DeleteSourceCommand.Execute(vm.SourceLists.Calendars.Single(c => c.Id == target.Id));

        var moved = test.Workspace.Events.Find("e1")!;
        var home = test.Workspace.Sources.FindCalendar(moved.CalendarId!);

        Assert.NotNull(home);
        Assert.True(CalendarWorkspace.IsLocal(home));
    }

    [Fact]
    public void 削除の確認では移った先を名前で言う()
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

        vm.DeleteSourceCommand.Execute(vm.SourceLists.Calendars.Single(c => c.Id == target.Id));

        // 「別のカレンダーへ移ります」だけでは、どこを見ればよいのか分からない
        Assert.Contains(CalendarWorkspace.DefaultCalendarName, editors.LastConfirmMessage, StringComparison.Ordinal);
        Assert.Contains(CalendarWorkspace.DefaultCalendarName, vm.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void inaCalendar_の名前は変えられない()
    {
        using var test = TestWorkspace.Create();

        var ina = test.Workspace.CreateCalendar(CalendarWorkspace.WorkingDayCalendarName);
        var (vm, editors) = Create(test);

        var locked = false;
        editors.OnCalendar = editor =>
        {
            locked = editor.IsNameLocked;

            // 画面で止めていても、別の入口から変えられては困る
            editor.Name = "べつの名前";
            editor.Color = "#8d6e63";
            return true;
        };

        vm.EditSourceCommand.Execute(vm.SourceLists.Calendars.Single(c => c.Id == ina.Id));

        Assert.True(locked);
        Assert.Equal(CalendarWorkspace.WorkingDayCalendarName,
            test.Workspace.Sources.FindCalendar(ina.Id)!.DisplayName);

        // 色は変えられる
        Assert.Equal("#8d6e63", test.Workspace.Sources.FindCalendar(ina.Id)!.BackgroundColor);
    }

    [Fact]
    public void ほかのカレンダーは今までどおり名前を変えられる()
    {
        using var test = TestWorkspace.Create();
        var (vm, editors) = Create(test);

        editors.OnCalendar = editor => { editor.Name = "私用"; return true; };
        vm.AddCalendarCommand.Execute(null);

        var target = vm.SourceLists.Calendars.Single(c => c.Name == "私用");

        editors.OnCalendar = editor =>
        {
            Assert.False(editor.IsNameLocked);
            editor.Name = "プライベート";
            return true;
        };
        vm.EditSourceCommand.Execute(vm.SourceLists.Calendars.Single(c => c.Id == target.Id));

        Assert.Equal("プライベート", test.Workspace.Sources.FindCalendar(target.Id)!.DisplayName);
    }

    // ------------------------------------------------------------------
    // 左パネルの並べ替え
    //
    // 並び順は時刻を持たない予定の並びにも効くので、見た目だけの話ではない
    // ------------------------------------------------------------------

    private static MainViewModel WithCalendars(TestWorkspace test, params string[] names)
    {
        foreach (var name in names) test.Workspace.CreateCalendar(name);
        return new MainViewModel(test.Workspace, today: D(2026, 9, 24));
    }

    [Fact]
    public void 並べ替えられる()
    {
        using var test = TestWorkspace.Create();
        var vm = WithCalendars(test, "仕事", "生産ライン", "社内行事");

        var before = vm.SourceLists.Calendars.Select(c => c.Name).ToArray();
        var moved = vm.SourceLists.Calendars.Single(c => c.Name == "社内行事");
        var target = vm.SourceLists.Calendars.Single(c => c.Name == "仕事");

        Assert.True(vm.MoveSource(moved, target, above: true));

        var after = vm.SourceLists.Calendars.Select(c => c.Name).ToArray();

        Assert.NotEqual(before, after);
        Assert.Equal("社内行事", after[Array.IndexOf(before, "仕事")]);
    }

    [Fact]
    public void 並べ替えは閉じても残る()
    {
        using var test = TestWorkspace.Create();
        var vm = WithCalendars(test, "仕事", "生産ライン");

        vm.MoveSource(
            vm.SourceLists.Calendars.Single(c => c.Name == "生産ライン"),
            vm.SourceLists.Calendars.Single(c => c.Name == "仕事"));

        var after = vm.SourceLists.Calendars.Select(c => c.Name).ToArray();

        // 開き直しても同じ並びで出る
        var again = new MainViewModel(test.Workspace, today: D(2026, 9, 24));
        Assert.Equal(after, again.SourceLists.Calendars.Select(c => c.Name));
    }

    [Fact]
    public void カレンダーとタスクリストの間では動かさない()
    {
        using var test = TestWorkspace.Create();
        var vm = WithCalendars(test, "仕事");

        var calendar = vm.SourceLists.Calendars[0];
        var list = vm.SourceLists.TaskLists[0];

        Assert.False(vm.CanMoveSource(calendar, list));
        Assert.False(vm.MoveSource(calendar, list));
    }

    [Fact]
    public void 同じところへ落としても何も起きない()
    {
        using var test = TestWorkspace.Create();
        var vm = WithCalendars(test, "仕事");

        var calendar = vm.SourceLists.Calendars[0];

        Assert.False(vm.CanMoveSource(calendar, calendar));
        Assert.False(vm.MoveSource(calendar, calendar));
    }

    [Fact]
    public void 並べ替えると時刻の無い予定の並びも変わる()
    {
        using var test = TestWorkspace.Create();

        var first = test.Workspace.CreateCalendar("社内行事");
        var second = test.Workspace.CreateCalendar("生産ライン");

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "行事", Date = D(2026, 9, 24), CalendarId = first.Id,
        });
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e2", Title = "点検", Date = D(2026, 9, 24), CalendarId = second.Id,
        });

        var vm = new MainViewModel(test.Workspace, today: D(2026, 9, 24));
        Assert.Equal(["e1", "e2"], vm.SelectedDay.Events.Select(e => e.Id));

        vm.MoveSource(
            vm.SourceLists.Calendars.Single(c => c.Id == second.Id),
            vm.SourceLists.Calendars.Single(c => c.Id == first.Id));

        Assert.Equal(["e2", "e1"], vm.SelectedDay.Events.Select(e => e.Id));
    }

    [Fact]
    public void 上に落とすか下に落とすかで入る位置が変わる()
    {
        using var test = TestWorkspace.Create();
        var vm = WithCalendars(test, "あ", "い", "う");

        var moved = vm.SourceLists.Calendars.Single(c => c.Name == "う");
        var target = vm.SourceLists.Calendars.Single(c => c.Name == "い");

        vm.MoveSource(moved, target, above: true);
        Assert.Equal(["あ", "う", "い"], Names(vm));

        // 戻してから下側に落とす
        vm.MoveSource(
            vm.SourceLists.Calendars.Single(c => c.Name == "う"),
            vm.SourceLists.Calendars.Single(c => c.Name == "い"),
            above: false);

        Assert.Equal(["あ", "い", "う"], Names(vm));
    }

    [Fact]
    public void 上へ動かしても位置がずれない()
    {
        using var test = TestWorkspace.Create();
        var vm = WithCalendars(test, "あ", "い", "う", "え");

        // いちばん下を、いちばん上の手前へ
        vm.MoveSource(
            vm.SourceLists.Calendars.Single(c => c.Name == "え"),
            vm.SourceLists.Calendars.Single(c => c.Name == "あ"),
            above: true);

        Assert.Equal(["え", "あ", "い", "う"], Names(vm));
    }

    [Fact]
    public void 掴んでいる間は落ちる位置を示す()
    {
        using var test = TestWorkspace.Create();
        var vm = WithCalendars(test, "あ", "い");

        var target = vm.SourceLists.Calendars.Single(c => c.Name == "い");

        vm.ShowDropHint(target, above: true);

        Assert.Equal(DropHint.Above, target.DropHint);
        Assert.All(vm.SourceLists.Calendars.Where(c => !ReferenceEquals(c, target)),
            c => Assert.Equal(DropHint.None, c.DropHint));

        vm.ShowDropHint(target, above: false);
        Assert.Equal(DropHint.Below, target.DropHint);

        // 外へ出たら消す
        vm.ShowDropHint(null, above: false);
        Assert.All(vm.SourceLists.Calendars, c => Assert.Equal(DropHint.None, c.DropHint));
    }

    [Fact]
    public void 落としたら示すのをやめる()
    {
        using var test = TestWorkspace.Create();
        var vm = WithCalendars(test, "あ", "い");

        var moved = vm.SourceLists.Calendars.Single(c => c.Name == "い");
        var target = vm.SourceLists.Calendars.Single(c => c.Name == "あ");

        vm.ShowDropHint(target, above: true);
        vm.MoveSource(moved, target, above: true);

        Assert.All(vm.SourceLists.Calendars, c => Assert.Equal(DropHint.None, c.DropHint));
    }

    /// <summary>作ったぶんだけ並びを見る。既定のマイカレンダーは先頭にいる。</summary>
    private static string[] Names(MainViewModel vm) =>
        vm.SourceLists.Calendars
            .Select(c => c.Name)
            .Where(n => n != CalendarWorkspace.DefaultCalendarName)
            .ToArray();
}
