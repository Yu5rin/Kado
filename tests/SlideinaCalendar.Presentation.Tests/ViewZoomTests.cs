using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// Ctrl＋ホイールでのビューの切り替え。
/// <para>
/// 一覧 → 年 → 月 → 週 → 日 の並びを1つずつ動く。回すたびに見ている範囲が
/// 狭まる（または広がる）。
/// </para>
/// </summary>
public class ViewZoomTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static MainViewModel Create(TestWorkspace test) =>
        new(test.Workspace, D(2026, 9, 24));

    [Fact]
    public void 細かいほうへ一つずつ動く()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.CurrentView = CalendarView.Agenda;

        vm.ZoomInCommand.Execute(null);
        Assert.Equal(CalendarView.Year, vm.CurrentView);

        vm.ZoomInCommand.Execute(null);
        Assert.Equal(CalendarView.Month, vm.CurrentView);

        vm.ZoomInCommand.Execute(null);
        Assert.Equal(CalendarView.Week, vm.CurrentView);

        vm.ZoomInCommand.Execute(null);
        Assert.Equal(CalendarView.Day, vm.CurrentView);
    }

    [Fact]
    public void 粗いほうへ一つずつ動く()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.CurrentView = CalendarView.Day;

        vm.ZoomOutCommand.Execute(null);
        Assert.Equal(CalendarView.Week, vm.CurrentView);

        vm.ZoomOutCommand.Execute(null);
        Assert.Equal(CalendarView.Month, vm.CurrentView);

        vm.ZoomOutCommand.Execute(null);
        Assert.Equal(CalendarView.Year, vm.CurrentView);

        vm.ZoomOutCommand.Execute(null);
        Assert.Equal(CalendarView.Agenda, vm.CurrentView);
    }

    [Fact]
    public void 端では止まる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.CurrentView = CalendarView.Day;
        vm.ZoomInCommand.Execute(null);

        // 回し続けて一覧と日を行き来されると、どこに居るのか見失う
        Assert.Equal(CalendarView.Day, vm.CurrentView);

        vm.CurrentView = CalendarView.Agenda;
        vm.ZoomOutCommand.Execute(null);
        Assert.Equal(CalendarView.Agenda, vm.CurrentView);
    }

    [Fact]
    public void 切り替えても選んでいる日はそのまま()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SelectedDate = D(2026, 11, 5);

        foreach (var view in new[]
                 {
                     CalendarView.Agenda, CalendarView.Year, CalendarView.Month,
                     CalendarView.Week, CalendarView.Day,
                 })
        {
            vm.CurrentView = view;

            Assert.Equal(D(2026, 11, 5), vm.SelectedDate);
        }
    }

    [Fact]
    public void 切り替えた先でも同じ日を見ている()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SelectedDate = D(2026, 11, 5);

        // 月から年へ移ったら、その日を含む年度が出ている
        vm.CurrentView = CalendarView.Year;
        Assert.Equal(2026, vm.Year.FiscalYear);
        Assert.Equal(D(2026, 11, 5), vm.Year.SelectedDate);

        // 年から日へ移ったら、その日が出ている
        vm.CurrentView = CalendarView.Day;
        Assert.Equal(D(2026, 11, 5), vm.Day.Date);

        // 週へ移ったら、その日を含む週が出ている
        vm.CurrentView = CalendarView.Week;
        Assert.True(vm.Week.WeekStart <= D(2026, 11, 5));
        Assert.True(D(2026, 11, 5) <= vm.Week.WeekStart.AddDays(6));

        // 月へ戻ったら、その月が出ている
        vm.CurrentView = CalendarView.Month;
        Assert.Equal(11, vm.Month.Month.Month);
    }

    [Fact]
    public void 年から選んだ日で月ビューへ移れる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.ShowMonthOfCommand.Execute(D(2027, 2, 10));

        Assert.Equal(CalendarView.Month, vm.CurrentView);
        Assert.Equal(D(2027, 2, 10), vm.SelectedDate);
        Assert.Equal(2, vm.Month.Month.Month);
    }

    [Fact]
    public void 年から選んだ日で日ビューへ移れる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.ShowDayOfCommand.Execute(D(2027, 2, 10));

        Assert.Equal(CalendarView.Day, vm.CurrentView);
        Assert.Equal(D(2027, 2, 10), vm.Day.Date);
    }
}
