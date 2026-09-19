using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

public class MainViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static MainViewModel Create(TestWorkspace test) =>
        new(test.Workspace, today: D(2026, 9, 24));

    [Fact]
    public void 起動時は今日が選ばれている()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.Equal(D(2026, 9, 24), vm.SelectedDate);
        Assert.Equal("2026年9月", vm.Title);
        Assert.Equal(CalendarView.Month, vm.CurrentView);
        Assert.True(vm.IsSidePanelOpen);
    }

    [Fact]
    public void 半期ビューは選べない()
    {
        // 要件書 5.1 は半期ビューを設けないとしている。5.2 の図には残っているが
        // そちらが廃止前の名残
        var views = Enum.GetNames<CalendarView>();

        Assert.Equal(["Month", "Week", "Day", "Year", "Agenda"], views);
        Assert.DoesNotContain("Half", views);
    }

    [Fact]
    public void 月を移動すると見出しが変わる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.NextCommand.Execute(null);
        Assert.Equal("2026年10月", vm.Title);

        vm.PreviousCommand.Execute(null);
        Assert.Equal("2026年9月", vm.Title);
    }

    [Fact]
    public void 今日へ戻ると選択も右ペインも追随する()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SelectedDate = D(2026, 9, 10);
        vm.NextCommand.Execute(null);

        vm.TodayCommand.Execute(null);

        Assert.Equal(D(2026, 9, 24), vm.SelectedDate);
        Assert.Equal(D(2026, 9, 24), vm.SelectedDay.Date);
        Assert.Equal("2026年9月", vm.Title);
    }

    [Fact]
    public void 日を選ぶと右ペインが入れ替わる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SelectDateCommand.Execute(D(2026, 9, 14));

        Assert.Equal(D(2026, 9, 14), vm.SelectedDay.Date);
        Assert.Equal("仕様期限", Assert.Single(vm.SelectedDay.Milestones).Name);
    }

    [Fact]
    public void 左パネルを折りたためる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.ToggleSidePanelCommand.Execute(null);
        Assert.False(vm.IsSidePanelOpen);

        vm.ToggleSidePanelCommand.Execute(null);
        Assert.True(vm.IsSidePanelOpen);
    }

    [Fact]
    public void 実働日のサマリーがツールバーに出る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 左パネルを閉じても情報が欠けないよう、ここに置く（要件書 5.2）
        Assert.Equal("今月の実働日 19日 ／ 残り 4日", vm.WorkingDaySummary);
    }

    [Fact]
    public void データが無い月では件数を数字で出さない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.NextCommand.Execute(null);   // 10月は登録範囲外

        // 0 と出すと「実働日が無い月」に見える
        Assert.Equal("実働日データ未登録", vm.WorkingDaySummary);
    }

    [Fact]
    public void 編集すると表示が引き直される()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.Empty(vm.SelectedDay.Events);

        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });

        // ワークスペースの通知を受けて、月ビューと右ペインの両方が追随する
        Assert.Single(vm.SelectedDay.Events);
        Assert.Single(vm.Month.Cells.Single(c => c.Date == D(2026, 9, 24)).Events);
    }

    [Fact]
    public void 元に戻すとその旨が出る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });

        Assert.True(vm.CanUndo);
        Assert.Equal("予定の追加を元に戻す", vm.UndoLabel);

        vm.UndoCommand.Execute(null);

        Assert.Equal("予定の追加を元に戻しました", vm.StatusMessage);
        Assert.Empty(vm.SelectedDay.Events);
        Assert.True(vm.CanRedo);
    }

    [Fact]
    public void やり直すとその旨が出る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });
        vm.UndoCommand.Execute(null);

        vm.RedoCommand.Execute(null);

        Assert.Equal("予定の追加をやり直しました", vm.StatusMessage);
        Assert.Single(vm.SelectedDay.Events);
    }

    [Fact]
    public void 戻すものが無ければコマンドは実行できない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
    }

    [Fact]
    public void ビューを切り替えられる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SwitchViewCommand.Execute(CalendarView.Week);
        Assert.Equal(CalendarView.Week, vm.CurrentView);
    }

    [Fact]
    public void 日付が変わると今日の位置が移る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.Today = D(2026, 9, 25);

        Assert.Equal(D(2026, 9, 25), vm.Month.Today);
        Assert.True(vm.Month.Cells.Single(c => c.Date == D(2026, 9, 25)).IsToday);
        Assert.Equal(3, vm.Month.RemainingWorkingDays);   // 28・29・30
    }
}
