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
        using var test = TestWorkspace.Create(withMilestones: true);
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

    [Fact]
    public void ツールバーは年と月を分けて出す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // モックの .month は年を一段小さく薄く出す
        Assert.Equal("2026", vm.TitleYear);
        Assert.Equal("9月", vm.TitleMonth);
    }

    [Fact]
    public void 実働日バッジは実働日数と残りを別々に出す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.True(vm.HasWorkingDayData);
        Assert.Equal("19", vm.WorkingDayCountText);      // 2026年9月の稼働日
        Assert.True(vm.HasRemainingWorkingDays);
        Assert.Equal("4", vm.RemainingWorkingDaysText);  // 9/24 の翌日から月末まで（25・28・29・30）
    }

    [Fact]
    public void 実働日データが無い月はバッジを出さない()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var vm = Create(test);

        // 数字だけ出すと、登録範囲外なのに実数だと思われる
        Assert.False(vm.HasWorkingDayData);
    }

    [Fact]
    public void 別の月へ動くと残りは出さない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.NextCommand.Execute(null);

        Assert.False(vm.HasRemainingWorkingDays);
        Assert.Equal(string.Empty, vm.RemainingWorkingDaysText);
    }

    [Fact]
    public void ミニ月暦は中央と独立して月を送れる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.MiniNextCommand.Execute(null);

        Assert.Equal("2026年10月", vm.MiniCalendar.Title);
        Assert.Equal("2026年9月", vm.Title);   // 中央は動かない
    }

    [Fact]
    public void 中央の月を送るとミニ月暦も合わせる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.NextCommand.Execute(null);

        Assert.Equal("2026年10月", vm.MiniCalendar.Title);
    }

    [Fact]
    public void 日を選ぶとミニ月暦の印も動く()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SelectDateCommand.Execute(D(2026, 9, 14));

        Assert.Equal(D(2026, 9, 14), vm.MiniCalendar.SelectedDate);
    }

    [Fact]
    public void ビューを切り替えると見ている日がそろう()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);
        vm.SelectedDate = D(2026, 9, 14);

        vm.SwitchViewCommand.Execute(CalendarView.Week);
        Assert.True(vm.IsWeekView);
        Assert.Equal(D(2026, 9, 13), vm.Week.WeekStart);   // 9/14 を含む週

        vm.SwitchViewCommand.Execute(CalendarView.Day);
        Assert.True(vm.IsDayView);
        Assert.Equal(D(2026, 9, 14), vm.Day.Date);
    }

    [Fact]
    public void 前後の移動はビューごとに幅が変わる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 月ビューでは月単位
        vm.NextCommand.Execute(null);
        Assert.Equal("2026年10月", vm.Title);
        vm.PreviousCommand.Execute(null);

        // 週ビューでは週単位
        vm.SwitchViewCommand.Execute(CalendarView.Week);
        vm.NextCommand.Execute(null);
        Assert.Equal(D(2026, 9, 27), vm.Week.WeekStart);

        // 日ビューでは日単位。週を送ったぶん、選んでいる日も曜日を保って
        // 10/1（木）へ移っているので、そこから1日進む
        vm.SwitchViewCommand.Execute(CalendarView.Day);
        Assert.Equal(D(2026, 10, 1), vm.Day.Date);

        vm.NextCommand.Execute(null);
        Assert.Equal(D(2026, 10, 2), vm.Day.Date);
    }

    [Fact]
    public void 送ると右ペインの日付も付いてくる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 置いていくと、中央は 9/18 を出しているのに右ペインは 9/22 のまま、
        // ということになる
        vm.SwitchViewCommand.Execute(CalendarView.Day);
        vm.NextCommand.Execute(null);
        Assert.Equal(vm.Day.Date, vm.SelectedDay.Date);

        // 月は日にちを保って翌月へ
        vm.SwitchViewCommand.Execute(CalendarView.Month);
        var before = vm.SelectedDate;
        vm.NextCommand.Execute(null);
        Assert.Equal(before.AddMonths(1), vm.SelectedDay.Date);

        // 週は曜日を保って翌週へ
        vm.SwitchViewCommand.Execute(CalendarView.Week);
        before = vm.SelectedDate;
        vm.NextCommand.Execute(null);
        Assert.Equal(before.AddDays(7), vm.SelectedDay.Date);

        // 年は同じ月日の翌年度へ
        vm.SwitchViewCommand.Execute(CalendarView.Year);
        before = vm.SelectedDate;
        vm.NextCommand.Execute(null);
        Assert.Equal(before.Month, vm.SelectedDay.Date.Month);
        Assert.Equal(before.Day, vm.SelectedDay.Date.Day);
        Assert.Equal(before.Year + 1, vm.SelectedDay.Date.Year);
    }

    [Fact]
    public void 週を送って月をまたぐと見出しも追いつく()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SwitchViewCommand.Execute(CalendarView.Week);
        vm.NextCommand.Execute(null);   // 9/27〜10/3
        vm.NextCommand.Execute(null);   // 10/4〜10/10

        // 見出しだけ前の月に残ると、どこを見ているのか分からない
        Assert.Equal("2026年10月", vm.Title);
        Assert.Equal("2026年10月", vm.MiniCalendar.Title);
    }

    [Fact]
    public void 今日へ戻るとどのビューも今日に合う()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SwitchViewCommand.Execute(CalendarView.Day);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);

        vm.TodayCommand.Execute(null);

        Assert.Equal(D(2026, 9, 24), vm.Day.Date);
        Assert.Equal(D(2026, 9, 20), vm.Week.WeekStart);
        Assert.Equal("2026年9月", vm.Title);
    }

    [Fact]
    public void 実働日データが無ければ残りのバッジも出さない()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var vm = Create(test);

        // データが無いのに「残り 0 日」と出ると、実数だと思われる
        Assert.False(vm.HasWorkingDayData);
        Assert.False(vm.HasRemainingWorkingDays);
    }

    [Fact]
    public void 同期の表示には同期の状態だけを出す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.Equal("Google 未接続", vm.SyncStatusText);

        // 操作の結果を混ぜると、同期できているのか読み取れなくなる
        vm.UndoCommand.Execute(null);
        Assert.Equal("Google 未接続", vm.SyncStatusText);
    }

    [Fact]
    public void 時計を進めると現在時刻の線が動く()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.UpdateNow(new DateTime(2026, 9, 24, 10, 0, 0));

        Assert.True(vm.Week.ShowNowLine);
        Assert.True(vm.Day.ShowNowLine);
    }

    [Fact]
    public void 日をまたぐと今日が差し替わる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 起動しっぱなしで日をまたぐ
        vm.UpdateNow(new DateTime(2026, 9, 25, 9, 0, 0));

        Assert.Equal(D(2026, 9, 25), vm.Today);
        Assert.Equal(D(2026, 9, 25), vm.Month.Today);
    }

    [Fact]
    public void ペインの幅は既定から始まる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.Equal(MainViewModel.DefaultSidePanelWidth, vm.SidePanelWidth);
        Assert.Equal(MainViewModel.DefaultDetailPaneWidth, vm.DetailPaneWidth);
    }

    [Fact]
    public void ペインの幅は次に開いたときも残る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SidePanelWidth = 260;
        vm.DetailPaneWidth = 340;

        // 起動し直した体
        var next = Create(test);

        Assert.Equal(260, next.SidePanelWidth);
        Assert.Equal(340, next.DetailPaneWidth);
    }

    [Fact]
    public void ペインの幅は収まる範囲に丸める()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 畳みきってしまうと中身が読めない。下限で止める
        vm.SidePanelWidth = 10;
        Assert.Equal(MainViewModel.MinSidePanelWidth, vm.SidePanelWidth);

        vm.DetailPaneWidth = 5000;
        Assert.Equal(MainViewModel.MaxDetailPaneWidth, vm.DetailPaneWidth);

        // 測りそこねた値は覚えない
        vm.SidePanelWidth = double.NaN;
        Assert.Equal(MainViewModel.MinSidePanelWidth, vm.SidePanelWidth);
    }
}
