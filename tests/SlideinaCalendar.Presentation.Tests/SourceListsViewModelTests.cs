using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

public class SourceListsViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static void Seed(TestWorkspace test)
    {
        var ws = test.Workspace;

        ws.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24), CalendarId = "仕事" });
        ws.AddEvent(new CalendarEvent { Id = "e2", Title = "点検", Date = D(2026, 9, 24), CalendarId = "生産ライン" });
        ws.AddEvent(new CalendarEvent { Id = "e3", Title = "所属なし", Date = D(2026, 9, 24) });

        ws.AddTask(new TaskItem { Id = "t1", Title = "提出", Due = D(2026, 9, 24), TaskListId = "マイタスク" });

        // 取り込みと同じで、持ち込まれた所属を一覧に起こす
        ws.EnsureSources();
    }

    [Fact]
    public void 何も無ければ既定の分類が用意される()
    {
        using var test = TestWorkspace.Create();

        var vm = new SourceListsViewModel(test.Workspace);

        // 予定が1件も無くても分類を先に作れるよう、入れ先を用意しておく
        Assert.Equal([CalendarWorkspace.DefaultCalendarName], vm.Calendars.Select(c => c.Name));
        Assert.Equal([CalendarWorkspace.DefaultTaskListName], vm.TaskLists.Select(t => t.Name));
    }

    [Fact]
    public void 持ち込まれた所属も一覧に起こされる()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        var vm = new SourceListsViewModel(test.Workspace);

        Assert.Contains("仕事", vm.Calendars.Select(c => c.Name));
        Assert.Contains("生産ライン", vm.Calendars.Select(c => c.Name));
        Assert.All(vm.Calendars, c => Assert.True(c.IsVisible));
    }

    [Fact]
    public void カレンダーを作れる()
    {
        using var test = TestWorkspace.Create();

        var created = test.Workspace.CreateCalendar("プライベート");
        var vm = new SourceListsViewModel(test.Workspace);

        Assert.Contains("プライベート", vm.Calendars.Select(c => c.Name));

        // 既存と同じ色にはしない。並べたときに見分けられなくなる
        Assert.DoesNotContain(created.BackgroundColor,
            vm.Calendars.Where(c => c.Id != created.Id).Select(c => c.SwatchColor));
    }

    [Fact]
    public void カレンダーの名前と色を変えられる()
    {
        using var test = TestWorkspace.Create();
        var created = test.Workspace.CreateCalendar("仮の名前");

        Assert.True(test.Workspace.UpdateCalendar(created.Id, "私用", "#8f5fa8"));

        var item = new SourceListsViewModel(test.Workspace).Calendars.Single(c => c.Id == created.Id);
        Assert.Equal("私用", item.Name);
        Assert.Equal("#8f5fa8", item.SwatchColor);
    }

    [Fact]
    public void カレンダーを消すと中の予定は別のカレンダーへ移る()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        var work = ws.CreateCalendar("仕事");
        ws.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24), CalendarId = work.Id });

        // 分類を消したかっただけなのに中身まで消えるのは行き過ぎ
        Assert.Equal(1, ws.DeleteCalendar(work.Id));

        Assert.DoesNotContain(work.Id, new SourceListsViewModel(ws).Calendars.Select(c => c.Id));
        Assert.NotNull(ws.Events.Find("e1"));
        Assert.NotEqual(work.Id, ws.Events.Find("e1")!.CalendarId);
    }

    [Fact]
    public void 最後のカレンダーは消せない()
    {
        using var test = TestWorkspace.Create();
        var only = Assert.Single(test.Workspace.Sources.Calendars());

        // 入れ先が無くなると、予定の所属が消えて分類できなくなる
        Assert.Null(test.Workspace.DeleteCalendar(only.Id));
    }

    [Fact]
    public void タスクリストも同じように作れる()
    {
        using var test = TestWorkspace.Create();

        var created = test.Workspace.CreateTaskList("計画業務");
        Assert.Contains("計画業務", new SourceListsViewModel(test.Workspace).TaskLists.Select(t => t.Name));

        Assert.True(test.Workspace.UpdateTaskList(created.Id, "計画業務（改）"));
        Assert.Contains("計画業務（改）",
            new SourceListsViewModel(test.Workspace).TaskLists.Select(t => t.Name));
    }

    [Fact]
    public void 色見本は同じ名前なら常に同じ色になる()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        var first = new SourceListsViewModel(test.Workspace);
        var second = new SourceListsViewModel(test.Workspace);

        Assert.Equal(first.Calendars[0].SwatchColor, second.Calendars[0].SwatchColor);
        Assert.StartsWith("#", first.Calendars[0].SwatchColor);
    }

    [Fact]
    public void チェックを外すとその所属の予定が落ちる()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        var vm = new SourceListsViewModel(test.Workspace);
        vm.Calendars.Single(c => c.Name == "仕事").IsVisible = false;

        var events = test.Workspace.Schedule.EventsInRange(D(2026, 9, 24), D(2026, 9, 24));

        Assert.DoesNotContain(events.Where(e => vm.IncludesEvent(e.Source)), e => e.Source.Id == "e1");
        Assert.Contains(events.Where(e => vm.IncludesEvent(e.Source)), e => e.Source.Id == "e2");
    }

    [Fact]
    public void 所属の無い予定は消さない()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        var vm = new SourceListsViewModel(test.Workspace);
        foreach (var calendar in vm.Calendars) calendar.IsVisible = false;

        // どこにも属していないだけで、消す理由にはならない
        Assert.True(vm.IncludesEvent(new CalendarEvent { Id = "e3", Date = D(2026, 9, 24) }));
    }

    [Fact]
    public void 読み直してもチェックの状態は残る()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        var vm = new SourceListsViewModel(test.Workspace);
        vm.Calendars.Single(c => c.Name == "仕事").IsVisible = false;

        vm.Refresh();

        Assert.False(vm.Calendars.Single(c => c.Name == "仕事").IsVisible);
        Assert.True(vm.Calendars.Single(c => c.Name == "生産ライン").IsVisible);
    }

    [Fact]
    public void チェックを外すと月ビューと右ペインから消える()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        var main = new MainViewModel(test.Workspace, today: D(2026, 9, 24));
        var cell = main.Month.Cells.Single(c => c.Date == D(2026, 9, 24));

        Assert.Equal(3, cell.AllEvents.Count);
        Assert.Equal(3, main.SelectedDay.Events.Count);

        main.SourceLists.Calendars.Single(c => c.Name == "仕事").IsVisible = false;

        var after = main.Month.Cells.Single(c => c.Date == D(2026, 9, 24));
        Assert.Equal(2, after.AllEvents.Count);
        Assert.Equal(2, main.SelectedDay.Events.Count);
    }

    [Fact]
    public void カレンダーの色を引ける()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        var vm = new SourceListsViewModel(test.Workspace);

        // 予定の色は所属カレンダーで決まる。1件ずつは選ばせない
        Assert.Equal(vm.Calendars.Single(c => c.Name == "仕事").SwatchColor, vm.ColorOf("仕事"));

        // 所属が無い・知らないカレンダーは既定の色に任せる
        Assert.Null(vm.ColorOf(null));
        Assert.Null(vm.ColorOf("知らない"));
    }

    [Fact]
    public void 予定の帯はカレンダーの色になる()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        var main = new MainViewModel(test.Workspace, today: D(2026, 9, 24));
        var expected = main.SourceLists.ColorOf("仕事");

        Assert.Equal(expected, main.SelectedDay.Events.Single(e => e.Id == "e1").Color);
        Assert.Equal(expected,
            main.Month.Cells.Single(c => c.Date == D(2026, 9, 24)).Events.Single(e => e.Id == "e1").Color);

        // 所属なしの予定は既定の色に任せる
        Assert.Null(main.SelectedDay.Events.Single(e => e.Id == "e3").Color);
    }

    [Fact]
    public void 取り込んだ一覧があればそちらを使う()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        test.Workspace.Sources.Upsert(new CalendarSource
        {
            Id = "work@example.com", Summary = "仕事用カレンダー",
            BackgroundColor = "#123456", SortOrder = 0, UpdatedAt = DateTimeOffset.Now,
        });

        var vm = new SourceListsViewModel(test.Workspace);

        // 名前も色も Google のものになる
        var item = vm.Calendars.Single(c => c.Id == "work@example.com");
        Assert.Equal("仕事用カレンダー", item.Name);
        Assert.Equal("#123456", item.SwatchColor);
        Assert.Equal("#123456", vm.ColorOf("work@example.com"));
    }

    [Fact]
    public void 付け替えた表示名があればそちらを出す()
    {
        using var test = TestWorkspace.Create();

        test.Workspace.Sources.Upsert(new CalendarSource
        {
            Id = "c1", Summary = "本名", SummaryOverride = "呼び名", UpdatedAt = DateTimeOffset.Now,
        });

        Assert.Equal("呼び名",
            new SourceListsViewModel(test.Workspace).Calendars.Single(c => c.Id == "c1").Name);
    }

    [Fact]
    public void 取り込んだ一覧のチェックは保存される()
    {
        using var test = TestWorkspace.Create();

        test.Workspace.Sources.Upsert(new CalendarSource
        {
            Id = "c1", Summary = "仕事", UpdatedAt = DateTimeOffset.Now,
        });

        var vm = new SourceListsViewModel(test.Workspace);
        vm.Calendars.Single(c => c.Id == "c1").IsVisible = false;

        // 同期のたびにチェックが戻ると使い物にならない（要件書 5.5）
        Assert.False(test.Workspace.Sources.Calendars().Single(c => c.Id == "c1").IsVisible);
        Assert.False(new SourceListsViewModel(test.Workspace).Calendars.Single(c => c.Id == "c1").IsVisible);
    }

    [Fact]
    public void 取り込んだ一覧でも絞り込みが効く()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        test.Workspace.Sources.Upsert(new CalendarSource
        {
            Id = "仕事", Summary = "仕事", UpdatedAt = DateTimeOffset.Now,
        });

        var vm = new SourceListsViewModel(test.Workspace);
        vm.Calendars.Single(c => c.Id == "仕事").IsVisible = false;

        Assert.False(vm.IncludesEvent(new CalendarEvent { Id = "e1", CalendarId = "仕事" }));
        Assert.True(vm.IncludesEvent(new CalendarEvent { Id = "e2", CalendarId = "生産ライン" }));
    }

    // ------------------------------------------------------------------
    // このアプリのものと Google のもの
    //
    // 左パネルで混ざっていると、消してよいのはどれか、名前を変えたら相手にも
    // 伝わるのはどれかが読めない。実機で見分けが付かないという指摘があった
    // ------------------------------------------------------------------

    /// <summary>Google から取り込んだ体のカレンダーを1件足す。</summary>
    private static void AddGoogleCalendar(TestWorkspace test, string id, string name) =>
        test.Workspace.Sources.Upsert(new CalendarSource
        {
            Id = id,
            Summary = name,
            BackgroundColor = "#2f6fed",

            // 取り込みは必ずこれを書く。これを持っているかどうかで見分ける
            GoogleRaw = $$"""{"id":"{{id}}","summary":"{{name}}","accessRole":"owner"}""",
            UpdatedAt = DateTimeOffset.Now,
        });

    [Fact]
    public void このアプリのものと_Google_のものを分けて並べる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.EnsureSources();
        AddGoogleCalendar(test, "yomeru@group.calendar.google.com", "仕事");

        var vm = new SourceListsViewModel(test.Workspace);

        Assert.Equal([CalendarWorkspace.DefaultCalendarName], vm.LocalCalendars.Select(c => c.Name));
        Assert.Equal(["仕事"], vm.GoogleCalendars.Select(c => c.Name));

        // 全部入りの一覧は今までどおり両方を持つ。色引きがこれを見ている
        Assert.Equal(2, vm.Calendars.Count);
    }

    [Fact]
    public void 両方あるときだけ見出しを出す()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.EnsureSources();

        var before = new SourceListsViewModel(test.Workspace);

        // 繋ぐ前は全部がこのアプリのもの。見出しが1つだけ立っても助けにならない
        Assert.False(before.ShowsCalendarGroups);

        AddGoogleCalendar(test, "yomeru@group.calendar.google.com", "仕事");
        var after = new SourceListsViewModel(test.Workspace);

        Assert.True(after.ShowsCalendarGroups);
    }

    [Fact]
    public void 印が付く前に作られたものもこのアプリのものと見なす()
    {
        using var test = TestWorkspace.Create();

        // 古い版は既定のカレンダーに local: の印を付けずに作っていた。
        // ID の形で決めると、これを Google のものと取り違える
        test.Workspace.Sources.Upsert(new CalendarSource
        {
            Id = "マイカレンダー", Summary = "マイカレンダー", UpdatedAt = DateTimeOffset.Now,
        });

        var vm = new SourceListsViewModel(test.Workspace);

        Assert.Contains("マイカレンダー", vm.LocalCalendars.Select(c => c.Id));
        Assert.Empty(vm.GoogleCalendars);
    }
}
