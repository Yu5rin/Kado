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
    }

    [Fact]
    public void 一覧は予定とタスクの所属から作られる()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        var vm = new SourceListsViewModel(test.Workspace);

        Assert.Equal(["仕事", "生産ライン"], vm.Calendars.Select(c => c.Name));
        Assert.Equal(["マイタスク"], vm.TaskLists.Select(t => t.Name));
        Assert.All(vm.Calendars, c => Assert.True(c.IsVisible));
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
        var item = Assert.Single(vm.Calendars);
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

        Assert.Equal("呼び名", Assert.Single(new SourceListsViewModel(test.Workspace).Calendars).Name);
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
        Assert.Single(vm.Calendars).IsVisible = false;

        // 同期のたびにチェックが戻ると使い物にならない（要件書 5.5）
        Assert.False(Assert.Single(test.Workspace.Sources.Calendars()).IsVisible);
        Assert.False(Assert.Single(new SourceListsViewModel(test.Workspace).Calendars).IsVisible);
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
        Assert.Single(vm.Calendars).IsVisible = false;

        Assert.False(vm.IncludesEvent(new CalendarEvent { Id = "e1", CalendarId = "仕事" }));
        Assert.True(vm.IncludesEvent(new CalendarEvent { Id = "e2", CalendarId = "生産ライン" }));
    }
}
