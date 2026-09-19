using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 月ビュー。テスト用のワークスペースは 2026年9月の稼働日（19日）を持つ。
/// </summary>
public class MonthViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static MonthViewModel Create(TestWorkspace test, DayOfWeek weekStart = DayOfWeek.Sunday) =>
        new(test.Workspace, D(2026, 9, 1), today: D(2026, 9, 24), weekStart);

    [Fact]
    public void 月初を含む週から月末を含む週までが並ぶ()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 2026/9/1 は火曜。日曜始まりなら 8/30 から。9/30 は水曜なので 10/3 まで
        Assert.Equal(D(2026, 8, 30), vm.Cells[0].Date);
        Assert.Equal(D(2026, 10, 3), vm.Cells[^1].Date);
        Assert.Equal(35, vm.Cells.Count);
    }

    [Fact]
    public void マスの数は週の数で決まる()
    {
        using var test = TestWorkspace.Create();

        // 2026年2月は日曜始まり。1日が日曜で28日なのでちょうど4週に収まる
        var february = new MonthViewModel(test.Workspace, D(2026, 2, 1), D(2026, 2, 15));

        // 6週に固定すると空の行が出て間延びする
        Assert.Equal(28, february.Cells.Count);
        Assert.All(february.Cells, c => Assert.True(c.IsCurrentMonth));
    }

    [Fact]
    public void 週の開始曜日を変えられる()
    {
        using var test = TestWorkspace.Create();

        var sunday = Create(test, DayOfWeek.Sunday);
        var monday = Create(test, DayOfWeek.Monday);

        Assert.Equal(["日", "月", "火", "水", "木", "金", "土"], sunday.WeekDayHeaders);
        Assert.Equal(["月", "火", "水", "木", "金", "土", "日"], monday.WeekDayHeaders);

        Assert.Equal(DayOfWeek.Sunday, sunday.Cells[0].Date.DayOfWeek);
        Assert.Equal(DayOfWeek.Monday, monday.Cells[0].Date.DayOfWeek);
    }

    [Fact]
    public void 前後の月にはみ出したマスが分かる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.False(vm.Cells[0].IsCurrentMonth);                       // 8/30
        Assert.True(vm.Cells.Single(c => c.Date == D(2026, 9, 1)).IsCurrentMonth);
        Assert.False(vm.Cells[^1].IsCurrentMonth);                      // 10/3
    }

    [Fact]
    public void 今日のマスが分かる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        var today = Assert.Single(vm.Cells, c => c.IsToday);
        Assert.Equal(D(2026, 9, 24), today.Date);
    }

    [Fact]
    public void 非稼働日は背景を沈める()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Cell(vm, D(2026, 9, 24)).Let(c => Assert.False(c.IsDimmed));   // 木・稼働日
        Cell(vm, D(2026, 9, 26)).Let(c => Assert.True(c.IsDimmed));    // 土
        Cell(vm, D(2026, 9, 21)).Let(c => Assert.True(c.IsDimmed));    // 敬老の日
    }

    [Fact]
    public void データ範囲の外は沈めない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 8/30 は登録範囲（9月）の外。稼働か非稼働かを判断できないので沈めない
        var outside = Cell(vm, D(2026, 8, 30));
        Assert.False(outside.HasWorkingDayData);
        Assert.False(outside.IsDimmed);
    }

    [Fact]
    public void 実働日データが無ければどこも沈まない()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var vm = Create(test);

        Assert.All(vm.Cells, c => Assert.False(c.IsDimmed));
    }

    [Fact]
    public void 土日の区別が付く()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.True(Cell(vm, D(2026, 9, 27)).IsSunday);
        Assert.True(Cell(vm, D(2026, 9, 26)).IsSaturday);
        Assert.False(Cell(vm, D(2026, 9, 24)).IsSunday);
    }

    [Fact]
    public void 予定とタスクがマスに配られる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });
        ws.AddTask(new TaskItem { Id = "t1", Title = "提出", Due = D(2026, 9, 24) });

        var vm = Create(test);

        var cell = Cell(vm, D(2026, 9, 24));
        Assert.Equal("会議", Assert.Single(cell.Events).Scheduled.Source.Title);
        Assert.Equal("提出", Assert.Single(cell.Tasks).Title);
        Assert.False(cell.IsEmpty);

        Assert.True(Cell(vm, D(2026, 9, 25)).IsEmpty);
    }

    [Fact]
    public void 繰り返し予定が各マスに現れる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "r1", Title = "定例", Date = D(2026, 9, 1), Recurrence = "FREQ=WEEKLY;BYDAY=TU",
        });

        var vm = Create(test);

        var days = vm.Cells.Where(c => c.Events.Count > 0).Select(c => c.Date).ToArray();
        Assert.Equal([D(2026, 9, 1), D(2026, 9, 8), D(2026, 9, 15), D(2026, 9, 22), D(2026, 9, 29)], days);
    }

    [Fact]
    public void マイルストーンがマスに出る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.Equal("仕様期限", Assert.Single(Cell(vm, D(2026, 9, 14)).Milestones).Name);
        Assert.Empty(Cell(vm, D(2026, 9, 15)).Milestones);
    }

    [Fact]
    public void 月を移動できる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.GoToNextMonth();
        Assert.Equal(D(2026, 10, 1), vm.Month);
        Assert.Equal("2026年10月", vm.Title);

        vm.GoToPreviousMonth();
        Assert.Equal(D(2026, 9, 1), vm.Month);
    }

    [Fact]
    public void 今日へ戻ると選択もそこへ移る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.GoToNextMonth();
        vm.GoToToday();

        Assert.Equal(D(2026, 9, 1), vm.Month);
        Assert.Equal(D(2026, 9, 24), vm.SelectedDate);
        Assert.True(Cell(vm, D(2026, 9, 24)).IsSelected);
    }

    [Fact]
    public void 選択は一つのマスだけに付く()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SelectedDate = D(2026, 9, 10);
        Assert.Equal(D(2026, 9, 10), Assert.Single(vm.Cells, c => c.IsSelected).Date);

        vm.SelectedDate = D(2026, 9, 11);
        Assert.Equal(D(2026, 9, 11), Assert.Single(vm.Cells, c => c.IsSelected).Date);
    }

    [Fact]
    public void 月をまたいでも選択が保たれる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SelectedDate = D(2026, 10, 1);   // 9月の表示にはみ出している 10/1
        vm.GoToNextMonth();

        Assert.True(Cell(vm, D(2026, 10, 1)).IsSelected);
    }

    [Fact]
    public void 実働日のサマリーが出る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.Equal(19, vm.WorkingDayCount);
        // 今日は 9/24。残りは 25・28・29・30 の 4 日
        Assert.Equal(4, vm.RemainingWorkingDays);
        Assert.True(vm.HasFullWorkingDayData);
    }

    [Fact]
    public void 今日を含まない月では残り日数を出さない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.GoToNextMonth();

        Assert.Null(vm.RemainingWorkingDays);
        // 10月は実働日データの範囲外なので、件数も参考値にならない
        Assert.False(vm.HasFullWorkingDayData);
    }

    [Fact]
    public void 編集したあと引き直せる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.True(Cell(vm, D(2026, 9, 24)).IsEmpty);

        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });
        vm.Refresh();

        Assert.Single(Cell(vm, D(2026, 9, 24)).Events);
    }

    // ------------------------------------------------------------------
    // マスの中身（モックの月グリッドに合わせた表示）
    // ------------------------------------------------------------------

    [Fact]
    public void 予定のチップに開始時刻が付く()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "計画レビュー", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 30),
        });

        var chip = Assert.Single(Cell(Create(test), D(2026, 9, 24)).Events);

        // 狭いマスで時刻とタイトルを別々に置くと、どちらも読めなくなる
        Assert.Equal("09:00 計画レビュー", chip.Label);
    }

    [Fact]
    public void 終日の予定には時刻が付かない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "棚卸", Date = D(2026, 9, 24) });

        Assert.Equal("棚卸", Assert.Single(Cell(Create(test), D(2026, 9, 24)).Events).Label);
    }

    [Fact]
    public void 複数日予定の2日目以降には時刻が付かない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "出張", Date = D(2026, 9, 24), EndDate = D(2026, 9, 25),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(18, 0),
        });

        var vm = Create(test);

        // 継続中の日に開始時刻を出すと、その日に始まるように見える
        Assert.Equal("09:00 出張", Assert.Single(Cell(vm, D(2026, 9, 24)).Events).Label);
        Assert.Equal("出張", Assert.Single(Cell(vm, D(2026, 9, 25)).Events).Label);
    }

    [Fact]
    public void 入りきらない予定は件数でまとめる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        for (var i = 1; i <= 5; i++)
        {
            ws.AddEvent(new CalendarEvent { Id = $"e{i}", Title = $"予定{i}", Date = D(2026, 9, 24) });
        }

        var cell = Cell(Create(test), D(2026, 9, 24));

        // 隠れたまま気づけないのを防ぐ
        Assert.Equal(DayCellViewModel.DefaultMaxChips, cell.Events.Count);
        Assert.Equal(2, cell.OverflowCount);
        Assert.Equal("＋2", cell.OverflowLabel);
        Assert.Equal(5, cell.AllEvents.Count);
    }

    [Fact]
    public void 入りきるときは件数を出さない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });

        var cell = Cell(Create(test), D(2026, 9, 24));

        Assert.Equal(0, cell.OverflowCount);
        Assert.Null(cell.OverflowLabel);
    }

    [Fact]
    public void 溢れたときは予定を先に見せる()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        for (var i = 1; i <= 3; i++)
        {
            ws.AddEvent(new CalendarEvent { Id = $"e{i}", Title = $"予定{i}", Date = D(2026, 9, 24) });
        }
        ws.AddTask(new TaskItem { Id = "t1", Title = "タスク", Due = D(2026, 9, 24) });

        var cell = Cell(Create(test), D(2026, 9, 24));

        // タスクは右ペインでも一覧できる
        Assert.Equal(3, cell.Events.Count);
        Assert.Empty(cell.Tasks);
        Assert.Equal(1, cell.OverflowCount);
    }

    [Fact]
    public void 祝日の名前が出る()
    {
        using var test = TestWorkspace.Create(
            holidays: new Dictionary<DateOnly, string> { [D(2026, 9, 21)] = "敬老の日" });

        var vm = Create(test);

        Assert.Equal("敬老の日", Cell(vm, D(2026, 9, 21)).HolidayName);
        Assert.Null(Cell(vm, D(2026, 9, 24)).HolidayName);
    }

    [Fact]
    public void 祝日データが無ければ名前は出ない()
    {
        using var test = TestWorkspace.Create();

        // 取り込むまでは何も返さない実装が入る
        Assert.Null(Cell(Create(test), D(2026, 9, 21)).HolidayName);
    }

    [Fact]
    public void 祝日名だけでもマスは空ではない()
    {
        using var test = TestWorkspace.Create(
            holidays: new Dictionary<DateOnly, string> { [D(2026, 9, 21)] = "敬老の日" });

        Assert.False(Cell(Create(test), D(2026, 9, 21)).IsEmpty);
    }

    private static DayCellViewModel Cell(MonthViewModel vm, DateOnly date) =>
        vm.Cells.Single(c => c.Date == date);
}

/// <summary>読みやすさのための小さな拡張。</summary>
internal static class LetExtensions
{
    public static void Let<T>(this T value, Action<T> action) => action(value);
}
