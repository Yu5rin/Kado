using Kado.Data.Models;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

public class MainViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static MainViewModel Create(TestWorkspace test) =>
        new(test.Workspace, today: D(2026, 9, 24));

    // ------------------------------------------------------------------
    // 初回起動だけの案内（項目7）
    // ------------------------------------------------------------------

    [Fact]
    public void 初回は案内を出し既読は覚える()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.True(vm.ShowsOnboarding);
        Assert.True(vm.ShowsStatusPill);

        vm.DismissOnboardingCommand.Execute(null);

        Assert.False(vm.ShowsOnboarding);
        Assert.False(vm.ShowsStatusPill);

        // 既読は workspace.Settings（DB）に書く。作り直しても再び出ない
        var again = Create(test);
        Assert.False(again.ShowsOnboarding);
    }

    [Fact]
    public void 案内の試すはスライドへ切り替えて既読にする()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.TryOnboardingCommand.Execute(null);

        Assert.False(vm.ShowsOnboarding);
        Assert.Equal(Kado.Presentation.Settings.ShellMode.Overlay, vm.Shell.Mode);
    }

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

    /// <summary>
    /// ミニ月暦の追従（項目9）。既定は中央に追従し、自分で送ったときだけ離れる。
    /// 中央を送ったら、また追従に戻る。
    /// </summary>
    [Fact]
    public void ミニ月暦から離れても中央を送ると追従に戻る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 自分で送って離れる
        vm.MiniNextCommand.Execute(null);
        Assert.Equal("2026年10月", vm.MiniCalendar.Title);

        // 中央だけをさらに送っても、離れたミニ月暦は動かない
        vm.NextCommand.Execute(null);
        Assert.Equal("2026年10月", vm.Title);
        Assert.Equal("2026年10月", vm.MiniCalendar.Title);

        vm.NextCommand.Execute(null);
        Assert.Equal("2026年11月", vm.Title);

        // 離れたままなら動かないはずが、動いた ＝ 追従に戻っている
        Assert.Equal("2026年11月", vm.MiniCalendar.Title);
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

    // ------------------------------------------------------------------
    // キーボードで日を選ぶ（項目8）
    //
    // ←→↑↓ の実際のキー入力は MainWindow.xaml.cs（WPF、Linux では検査できない）が
    // 受けるが、動かす先は MainViewModel.MoveSelection に一本化してあるので、
    // ここで押さえられる
    // ------------------------------------------------------------------

    [Fact]
    public void 矢印キー相当でその日から1日ずつ動く()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);
        vm.SelectedDate = D(2026, 9, 14);

        vm.MoveSelection(1);
        Assert.Equal(D(2026, 9, 15), vm.SelectedDate);

        vm.MoveSelection(-1);
        vm.MoveSelection(-1);
        Assert.Equal(D(2026, 9, 13), vm.SelectedDate);
    }

    [Fact]
    public void 上下キー相当で1週間ずつ動く()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);
        vm.SelectedDate = D(2026, 9, 14);

        vm.MoveSelection(7);
        Assert.Equal(D(2026, 9, 21), vm.SelectedDate);

        vm.MoveSelection(-7);
        vm.MoveSelection(-7);
        Assert.Equal(D(2026, 9, 7), vm.SelectedDate);
    }

    [Fact]
    public void 月をまたいで動くとビューも追いかける()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);
        vm.SelectedDate = D(2026, 9, 28);

        vm.MoveSelection(7);

        Assert.Equal(D(2026, 10, 5), vm.SelectedDate);
        Assert.Equal("2026年10月", vm.Title);
        Assert.Equal(D(2026, 10, 1), vm.Month.Month);

        // ミニ月暦も追従したまま（項目9）
        Assert.Equal("2026年10月", vm.MiniCalendar.Title);
    }

    // ------------------------------------------------------------------
    // 幅に合わせた詰め方
    //
    // 細い帯として使うので、入りきらないものは順に落とす
    // ------------------------------------------------------------------

    private static void Fit(MainViewModel vm, double width)
    {
        vm.Shell.LayoutWidth = width;
        vm.FitTo(width);
    }

    [Fact]
    public void パネルの組は居かたごとに覚える()
    {
        using var test = TestWorkspace.Create();
        var settings = new Kado.Presentation.Settings.AppSettings(test.Workspace.Settings);
        var vm = new MainViewModel(test.Workspace, today: D(2026, 9, 24), settings: settings);

        // ウィンドウでは3ペイン
        Assert.True(vm.IsSidePanelOpen);
        Assert.True(vm.IsMainViewOpen);
        Assert.True(vm.IsDetailPaneOpen);

        vm.ToggleSidePanelCommand.Execute(null);

        // 画面端へ寄せると、そちらの組に入れ替わる
        vm.Shell.ToggleSlideCommand.Execute(null);
        Assert.False(vm.IsMainViewOpen);
        Assert.True(vm.IsDetailPaneOpen);

        // 端の組でも左パネルを開いてみる
        vm.ToggleSidePanelCommand.Execute(null);

        // ウィンドウへ戻すと、さっきの組が戻る
        vm.Shell.ToggleSlideCommand.Execute(null);
        Assert.False(vm.IsSidePanelOpen);
        Assert.True(vm.IsMainViewOpen);

        // もう一度寄せれば、帯のほうの組（端で開いた左パネルが戻る）
        vm.Shell.ToggleSlideCommand.Execute(null);
        Assert.False(vm.IsMainViewOpen);
        Assert.True(vm.IsSidePanelOpen);
    }

    // ------------------------------------------------------------------
    // パネル組の移行（旧4文字「左・中央・右・スリム」→ 新3文字「左・中央・右」）
    // ------------------------------------------------------------------

    /// <summary>
    /// 旧スリム利用者（4文字目が '1'）は、右パネルを開いた状態に畳み込み、
    /// 月カレンダーの「畳んだ」既定も上書きして開いた状態にする（ウィンドウの組）。
    /// </summary>
    [Fact]
    public void 旧4文字でスリムが開いていたらウィンドウの組は右パネルを開いて月カレンダーも開く()
    {
        using var test = TestWorkspace.Create();
        var settings = new Kado.Presentation.Settings.AppSettings(test.Workspace.Settings);

        // 旧スリム利用者：左パネルは閉じ、中央は開き、右パネルは閉じ、スリムは開いていた
        settings.WindowPanes = "0101";

        var vm = new MainViewModel(test.Workspace, today: D(2026, 9, 24), settings: settings);

        Assert.False(vm.IsSidePanelOpen);
        Assert.True(vm.IsMainViewOpen);

        // 右パネルは畳み込みで開く（スリムの中身がそのまま移ったことになる）
        Assert.True(vm.IsDetailPaneOpen);

        // 既定（畳んだ状態）を上書きし、開いた状態にする
        Assert.False(vm.IsPaneCalendarCollapsed);

        // 3文字の新しい形へ保存し直してある
        Assert.Equal("011", settings.WindowPanes);
    }

    /// <summary>端に寄せているときの組（EdgePanes）にも同じ移行が効く。</summary>
    [Fact]
    public void 旧4文字でスリムが開いていたら端の組も右パネルを開いて月カレンダーも開く()
    {
        using var test = TestWorkspace.Create();
        var settings = new Kado.Presentation.Settings.AppSettings(test.Workspace.Settings);

        settings.EdgePanes = "0011";

        var atEdge = new Kado.Presentation.Settings.DockPlacement(
            Kado.Presentation.Settings.ShellMode.Overlay,
            Kado.Presentation.Settings.DockEdge.Left, 300, null);
        var vm = new MainViewModel(test.Workspace, today: D(2026, 9, 24), settings: settings, shell: atEdge);

        Assert.False(vm.IsSidePanelOpen);
        Assert.False(vm.IsMainViewOpen);
        Assert.True(vm.IsDetailPaneOpen);
        Assert.False(vm.IsPaneCalendarCollapsed);
        Assert.Equal("001", settings.EdgePanes);
    }

    /// <summary>
    /// 旧4文字でもスリムが閉じていた（4文字目が '0'）なら、右パネルへの畳み込みも
    /// 月カレンダーの上書きも起きない。左・中央・右の3つをそのまま引き継ぐだけ。
    /// </summary>
    [Fact]
    public void 旧4文字でもスリムが閉じていたら普通に3つを引き継ぐ()
    {
        using var test = TestWorkspace.Create();
        var settings = new Kado.Presentation.Settings.AppSettings(test.Workspace.Settings);

        settings.WindowPanes = "1100";

        var vm = new MainViewModel(test.Workspace, today: D(2026, 9, 24), settings: settings);

        Assert.True(vm.IsSidePanelOpen);
        Assert.True(vm.IsMainViewOpen);
        Assert.False(vm.IsDetailPaneOpen);

        // 移行していないので、月カレンダーの既定（畳んだ状態）はそのまま
        Assert.True(vm.IsPaneCalendarCollapsed);
    }

    /// <summary>新しい3文字の形はそのまま読める。</summary>
    [Fact]
    public void 新3文字の組はそのまま読める()
    {
        using var test = TestWorkspace.Create();
        var settings = new Kado.Presentation.Settings.AppSettings(test.Workspace.Settings);

        settings.WindowPanes = "010";

        var vm = new MainViewModel(test.Workspace, today: D(2026, 9, 24), settings: settings);

        Assert.False(vm.IsSidePanelOpen);
        Assert.True(vm.IsMainViewOpen);
        Assert.False(vm.IsDetailPaneOpen);
        Assert.True(vm.IsPaneCalendarCollapsed);
    }

    [Fact]
    public void 狭くしてもパネルは勝手に畳まない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Fit(vm, 1200);
        Assert.True(vm.IsSidePanelOpen);
        Assert.True(vm.IsDetailPaneOpen);

        // 出しておきたくて出しているものが、幅の都合で消えるのは筋が悪い。
        // 入りきらないぶんは切れるだけにして、何を出すかは手で決めてもらう
        Fit(vm, 320);

        Assert.True(vm.IsSidePanelOpen);
        Assert.True(vm.IsMainViewOpen);
        Assert.True(vm.IsDetailPaneOpen);
    }

    [Fact]
    public void 狭いとツールバーの中身を順に落とす()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Fit(vm, 1200);
        Assert.True(vm.ShowsWorkdayBadges);
        Assert.True(vm.ShowsSyncStatus);
        Assert.True(vm.ShowsSearchBox);
        Assert.False(vm.UsesCompactSearch);
        Assert.True(vm.ShowsViewSwitcher);
        Assert.False(vm.UsesCompactTodayButton);

        // 実働・残りのバッジがいちばん先。同じ数字は右ペインにも出ている
        Fit(vm, 950);
        Assert.False(vm.ShowsWorkdayBadges);
        Assert.True(vm.ShowsSyncStatus);

        Fit(vm, 890);
        Assert.False(vm.ShowsSyncStatus);
        Assert.False(vm.UsesCompactSearch);

        // 検索は消さずに虫めがねへ畳む
        Fit(vm, 800);
        Assert.True(vm.UsesCompactSearch);
        Assert.False(vm.ShowsSearchBox);

        vm.OpenSearchCommand.Execute(null);
        Assert.True(vm.ShowsSearchBox);
        vm.ClearSearch();
        Assert.False(vm.ShowsSearchBox);

        Fit(vm, 700);
        Assert.False(vm.ShowsViewSwitcher);
        Assert.False(vm.UsesCompactTodayButton);

        // いちばん細いところでも「今日」は消さない。アイコンだけに畳む（項目10）
        Fit(vm, 340);
        Assert.True(vm.UsesCompactTodayButton);
    }

    /// <summary>
    /// 右列を「…」に畳む（項目5）。OverflowFloor（360px）を切ると 🔍・▥・⚙ を
    /// 1個にまとめる。📌 と出しかた（ウィンドウ⇔スライド）は戻り口として
    /// ShowsWorkdayBadges 等とは別に常に出るので、ここでは扱わない（項目4。
    /// MainWindow.xaml で Visibility を結ばず常時表示にしてある）。
    /// </summary>
    [Fact]
    public void とても狭いと右列を畳みボタン1個にまとめる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Fit(vm, 380);
        Assert.False(vm.UsesOverflowMenu);
        Assert.True(vm.ShowsCompactSearchIcon);

        Fit(vm, 359);
        Assert.True(vm.UsesOverflowMenu);

        // 検索も「…」へ集約するので、畳んだ虫めがねは出さない
        Assert.False(vm.ShowsCompactSearchIcon);

        Fit(vm, 360);
        Assert.False(vm.UsesOverflowMenu);
    }

    [Fact]
    public void 週ビューでも選んだ日に印が付く()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SwitchViewCommand.Execute(CalendarView.Week);
        vm.SelectDateCommand.Execute(D(2026, 9, 22));

        // 右ペインとの対応が分かるよう、列の見出しにも印を出す
        Assert.Equal(D(2026, 9, 22), vm.SelectedDay.Date);
        Assert.Single(vm.Week.Days, d => d.IsSelected);
        Assert.True(vm.Week.Days.Single(d => d.Date == D(2026, 9, 22)).IsSelected);

        // 週を送っても、印は選んでいる日に付いたまま
        vm.NextCommand.Execute(null);
        Assert.Single(vm.Week.Days, d => d.IsSelected);
        Assert.True(vm.Week.Days.Single(d => d.Date == D(2026, 9, 29)).IsSelected);
    }

    [Fact]
    public void 前後の月を選ぶと右パネルの月カレンダーもその月へ移る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.Equal(9, vm.PaneMonth.Month.Month);

        // 月ビューは前後の月のマスも出す。そこを押したのに9月のままでは、
        // どこを選んだのか分からない
        vm.SelectDateCommand.Execute(D(2026, 10, 1));

        Assert.Equal(10, vm.PaneMonth.Month.Month);
        Assert.Equal("10月", vm.PaneTitleMonth);
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
