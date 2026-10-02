using Kado.Presentation.Infrastructure;
using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 起動しっぱなしで日付をまたいだときの動き。
/// <para>
/// 閉じるボタンでトレイに入る作りが既定で、ログオン時に自動で起動もするので、何日も
/// 動き続ける。以前は「今日」の印だけが新しい日へ移り、選んでいる日・右パネル・中央の
/// 表示位置・ミニ月暦と右パネルの月カレンダーの表示月は前日のまま残っていた。
/// </para>
/// <para>
/// 選んでいたのが前の「今日」なら新しい今日へ連れていく。別の日を選んでいたなら、
/// 見ている途中で動かさない。
/// </para>
/// </summary>
public class DateRolloverTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static MainViewModel Create(TestWorkspace test, DateOnly today) =>
        new(test.Workspace, today, settings: new AppSettings(test.Workspace.Settings) { IsPaneCalendarCollapsed = false });

    // ------------------------------------------------------------------
    // 前の「今日」を選んでいた
    // ------------------------------------------------------------------

    [Fact]
    public void 前日を選んでいたら翌日へ移り右パネルの日付も追従する()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));

        vm.UpdateNow(new DateTime(2026, 9, 25, 0, 1, 0));

        Assert.Equal(D(2026, 9, 25), vm.Today);
        Assert.Equal(D(2026, 9, 25), vm.SelectedDate);
        Assert.Equal(D(2026, 9, 25), vm.SelectedDay.Date);
        Assert.Equal(D(2026, 9, 25), vm.Month.SelectedDate);
        Assert.Equal(D(2026, 9, 25), vm.MiniCalendar.SelectedDate);
        Assert.Equal(D(2026, 9, 25), vm.PaneMonth.SelectedDate);
        Assert.Equal(D(2026, 9, 25), vm.Day.Date);
        Assert.True(vm.PaneMonth.Cells.Single(c => c.Date == D(2026, 9, 25)).IsToday);
    }

    [Fact]
    public void 月末から翌月1日へ移ると月の表示も新しい月へ移る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 30));

        vm.UpdateNow(new DateTime(2026, 10, 1, 0, 1, 0));

        Assert.Equal(D(2026, 10, 1), vm.SelectedDate);
        Assert.Equal(10, vm.Month.Month.Month);
        Assert.Equal(10, vm.MiniCalendar.Month.Month);
        Assert.Equal(10, vm.PaneMonth.Month.Month);
        Assert.Equal(D(2026, 10, 1), vm.Day.Date);
        Assert.Contains(D(2026, 10, 1), Enumerable.Range(0, 7).Select(i => vm.Week.WeekStart.AddDays(i)));
        Assert.Equal("10", vm.PaneTitleMonth.TrimEnd('月'));
    }

    [Fact]
    public void 年を作ってあるときは新しい今日を含む年度へ移る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2027, 3, 31));
        vm.CurrentView = CalendarView.Year;
        Assert.Equal(2026, vm.Year.FiscalYear);

        vm.UpdateNow(new DateTime(2027, 4, 1, 0, 1, 0));

        Assert.Equal(2027, vm.Year.FiscalYear);
        Assert.Equal(D(2027, 4, 1), vm.Year.SelectedDate);
    }

    [Fact]
    public void 一覧を作ってあるときは新しい今日の行へ移る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 30));
        vm.CurrentView = CalendarView.Agenda;
        Assert.True(vm.Agenda.RowOn(D(2026, 9, 30))!.IsToday);

        vm.UpdateNow(new DateTime(2026, 10, 1, 0, 1, 0));

        Assert.True(vm.Agenda.RowOn(D(2026, 10, 1))!.IsToday);
        Assert.Equal(D(2026, 10, 1), vm.Agenda.SelectedDate);
    }

    // ------------------------------------------------------------------
    // 別の日を選んでいた
    // ------------------------------------------------------------------

    [Fact]
    public void 別の日を選んでいたら選択も表示位置も動かず今日の印だけ移る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 30));
        vm.SelectedDate = D(2026, 9, 10);

        vm.UpdateNow(new DateTime(2026, 10, 1, 0, 1, 0));

        Assert.Equal(D(2026, 10, 1), vm.Today);
        Assert.Equal(D(2026, 10, 1), vm.Month.Today);
        Assert.True(vm.PaneMonth.Cells.Single(c => c.Date == D(2026, 10, 1)).IsToday);

        Assert.Equal(D(2026, 9, 10), vm.SelectedDate);
        Assert.Equal(D(2026, 9, 10), vm.SelectedDay.Date);
        Assert.Equal(D(2026, 9, 10), vm.PaneMonth.SelectedDate);
        Assert.Equal(9, vm.Month.Month.Month);
        Assert.Equal(9, vm.MiniCalendar.Month.Month);
        Assert.Equal(9, vm.PaneMonth.Month.Month);
    }

    [Fact]
    public void 日付が同じなら選択は何も動かない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));
        vm.SelectedDate = D(2026, 9, 10);

        vm.UpdateNow(new DateTime(2026, 9, 24, 23, 59, 0));

        Assert.Equal(D(2026, 9, 10), vm.SelectedDate);
        Assert.Equal(D(2026, 9, 24), vm.Today);
    }

    // ------------------------------------------------------------------
    // 確かめ直すきっかけ
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(ClockChange.Resume, true)]
    [InlineData(ClockChange.TimeChanged, true)]
    [InlineData(ClockChange.Suspend, false)]
    [InlineData(ClockChange.PowerStatus, false)]
    public void スリープからの復帰と時計の変更だけすぐ確かめ直す(ClockChange change, bool expected) =>
        Assert.Equal(expected, ClockChangeRules.ShouldRecheck(change));
}
