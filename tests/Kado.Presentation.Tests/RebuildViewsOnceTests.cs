using Kado.Data.Models;
using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;
using Microsoft.Data.Sqlite;

namespace Kado.Presentation.Tests;

/// <summary>
/// 設定（週の始まり・表示時間帯）が変わってビューを組み直すとき、作りたてのビューを
/// 直後にもう一度組み直さない。
/// <para>
/// 月・週・日・ミニ月暦・右パネルの月は、作るときに最新を読む。以前は組み直しの直後に
/// <c>RefreshViews</c> が全部をもう一度読み直していた（予定の読み込みが2倍）。
/// </para>
/// </summary>
public class RebuildViewsOnceTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    /// <summary>events の表を読んだ行数を数える。数えるあいだだけ、表を覆う一時ビューを置く。</summary>
    private sealed class EventReadCounter : IDisposable
    {
        private readonly SqliteConnection _connection;
        private int _rows;

        public EventReadCounter(SqliteConnection connection)
        {
            _connection = connection;
            connection.CreateFunction("kado_event_probe", () => { Interlocked.Increment(ref _rows); return 1; });

            using var create = connection.CreateCommand();
            create.CommandText =
                "CREATE TEMP VIEW events AS SELECT * FROM main.events WHERE kado_event_probe() = 1;";
            create.ExecuteNonQuery();
        }

        public int Rows => Volatile.Read(ref _rows);

        public void Reset() => Interlocked.Exchange(ref _rows, 0);

        public void Dispose()
        {
            using var drop = _connection.CreateCommand();
            drop.CommandText = "DROP VIEW IF EXISTS temp.events;";
            drop.ExecuteNonQuery();
        }
    }

    private static void Seed(TestWorkspace test)
    {
        for (var i = 0; i < 40; i++)
        {
            test.Workspace.AddEvent(new CalendarEvent
            {
                Id = $"e{i}", Title = $"予定{i}", Date = new DateOnly(2026, 9, 1).AddDays(i % 30),
                StartTime = new TimeOnly(9 + i % 8, 0), EndTime = new TimeOnly(10 + i % 8, 0),
            });
        }
    }

    [Fact]
    public void 週の始まりを変えて組み直しても_予定を読むのは作りたての分だけ()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        var settings = new AppSettings(test.Workspace.Settings);
        var vm = new MainViewModel(test.Workspace, Today, settings: settings);

        using var counter = new EventReadCounter(test.Connection);

        // 比べる相手：同じ組み立てを新しく作るのにかかる読み込み。選んでいる日の一覧は組み直さないが引き直す
        counter.Reset();
        var weekStart = settings.WeekStart == DayOfWeek.Monday ? DayOfWeek.Sunday : DayOfWeek.Monday;
        var month = new MonthViewModel(test.Workspace, Today, Today, weekStart, sources: vm.SourceLists);
        var pane = new MonthViewModel(test.Workspace, Today, Today, weekStart, sources: vm.SourceLists);
        var mini = new MiniCalendarViewModel(test.Workspace, Today, Today, weekStart);
        var week = new WeekViewModel(test.Workspace, Today, Today, weekStart, vm.SourceLists);
        var day = new DayViewModel(test.Workspace, Today, Today, vm.SourceLists);
        var freshBuild = counter.Rows;

        counter.Reset();
        vm.SelectedDay.Refresh();
        var selectedDayRefresh = counter.Rows;

        Assert.True(freshBuild > 0, "読んだ回数を数えられていない");

        // 組み直す
        counter.Reset();
        settings.WeekStart = weekStart;
        var rebuild = counter.Rows;

        GC.KeepAlive((month, pane, mini, week, day));

        // 作りたてを直後に読み直していれば、ほぼ2倍になる
        Assert.True(
            rebuild <= freshBuild + selectedDayRefresh,
            $"組み直しで {rebuild} 行読んだ。新しく作る分は {freshBuild}、選んだ日の引き直しは {selectedDayRefresh}");
    }

    [Fact]
    public void 組み直したあとも_表示内容は新しい設定のものになっている()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        var settings = new AppSettings(test.Workspace.Settings);
        var vm = new MainViewModel(test.Workspace, Today, settings: settings);

        var before = vm.Month;
        settings.WeekStart = DayOfWeek.Monday;

        // 新しく作り直されている
        Assert.NotSame(before, vm.Month);
        Assert.Equal(DayOfWeek.Monday, vm.Month.WeekDayHeaders[0].DayOfWeek);
        Assert.Equal(DayOfWeek.Monday, vm.PaneMonth.WeekDayHeaders[0].DayOfWeek);
        Assert.Equal(DayOfWeek.Monday, vm.Week.WeekStart.DayOfWeek);

        // 中身は読み直した結果と同じ（作りたてのまま古くならない）
        Assert.Equal(40, vm.Month.Cells.Where(c => c.Date.Month == 9).Sum(c => c.AllEvents.Count));
        var fresh = new MonthViewModel(test.Workspace, vm.Month.Month, Today, DayOfWeek.Monday, sources: vm.SourceLists);
        Assert.Equal(
            fresh.Cells.Select(c => (c.Date, c.AllEvents.Count)),
            vm.Month.Cells.Select(c => (c.Date, c.AllEvents.Count)));
    }

    [Fact]
    public void 組み直しで年と一覧を作らず_作ってあれば作り直して印を下ろす()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        var settings = new AppSettings(test.Workspace.Settings);
        var vm = new MainViewModel(test.Workspace, Today, settings: settings);

        // 作っていないものは作らない（見えていない表示は作り直さない）
        settings.WeekStart = DayOfWeek.Monday;
        Assert.Null(vm.YearForView);
        Assert.Null(vm.AgendaForView);

        // 作ってあれば、週の始まりを引き継いで作り直す
        vm.CurrentView = CalendarView.Year;
        var year = vm.Year;
        vm.CurrentView = CalendarView.Month;

        settings.WeekStart = DayOfWeek.Sunday;
        Assert.NotSame(year, vm.Year);

        // 出し直しのとき、作りたてをもう一度組み直さない（印が下りている）
        using var counter = new EventReadCounter(test.Connection);
        counter.Reset();
        vm.CurrentView = CalendarView.Year;
        var switchRows = counter.Rows;

        counter.Reset();
        vm.CurrentView = CalendarView.Month;
        vm.CurrentView = CalendarView.Week;
        vm.CurrentView = CalendarView.Day;
        var othersRows = counter.Rows;

        Assert.Equal(0, switchRows);
        Assert.Equal(0, othersRows);
    }

    [Fact]
    public void データが変わったときの引き直しは今までどおり全部を読み直す()
    {
        using var test = TestWorkspace.Create();
        Seed(test);

        var vm = new MainViewModel(test.Workspace, Today);
        var cell = vm.Month.Cells.Single(c => c.Date == new DateOnly(2026, 9, 5));
        var before = cell.AllEvents.Count;

        test.Workspace.AddEvent(new CalendarEvent { Id = "new", Title = "追加", Date = new DateOnly(2026, 9, 5) });

        Assert.Equal(before + 1, vm.Month.Cells.Single(c => c.Date == new DateOnly(2026, 9, 5)).AllEvents.Count);
    }
}
