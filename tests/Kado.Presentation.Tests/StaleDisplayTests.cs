using Kado.Core.WorkingDays;
using Kado.Data.Models;
using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Kado.Presentation.Tests;

/// <summary>
/// 何日も動き続けるあいだに、古いまま残る表示・状態。
/// <para>
/// 日付をまたぐ・設定を変える・データが変わる・パネルを畳む、といった操作のあとで、
/// 見出しや選択の印、現在時刻の線、検索結果、押せるかどうかが前のままにならないことを確かめる。
/// 「見えていないビューは作り直さない」（<c>_xxxStale</c>）の決まりは崩さない。
/// </para>
/// </summary>
public class StaleDisplayTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static TimeOnly T(int h, int m = 0) => new(h, m);

    private static MainViewModel Create(TestWorkspace test, DateOnly today, AppSettings? settings = null) =>
        new(test.Workspace, today, settings: settings);

    private static AppSettings Settings(TestWorkspace test) => new(test.Workspace.Settings);

    /// <summary>プロパティの変更通知を、名前の列として控える。</summary>
    private static List<string> Watch(System.ComponentModel.INotifyPropertyChanged source)
    {
        var names = new List<string>();
        source.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);
        return names;
    }

    private static void AddEventsOn(TestWorkspace test, params DateOnly[] dates)
    {
        foreach (var date in dates)
        {
            test.Workspace.AddEvent(new CalendarEvent
            {
                Id = $"e{date:yyyyMMdd}", Title = "予定", Date = date,
            });
        }
    }

    // ------------------------------------------------------------------
    // 項目2: 「今日」・日付またぎで、週と一覧の選択の印が前の日のまま残る
    // ------------------------------------------------------------------

    [Fact]
    public void 今日へ戻ると週の選択の印も今日へ移る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));
        vm.CurrentView = CalendarView.Week;
        vm.SelectedDate = D(2026, 9, 22);
        Assert.Equal(D(2026, 9, 22), vm.Week.Days.Single(d => d.IsSelected).Date);

        vm.TodayCommand.Execute(null);

        Assert.Equal(D(2026, 9, 24), vm.Week.SelectedDate);
        Assert.Equal(D(2026, 9, 24), vm.Week.Days.Single(d => d.IsSelected).Date);
    }

    [Fact]
    public void 今日へ戻ると一覧の選択の印も今日の行へ移る()
    {
        using var test = TestWorkspace.Create();

        // 予定のある日は1行ずつ（無い日は連続でひとまとめの行になる）
        AddEventsOn(test, D(2026, 9, 10), D(2026, 9, 24));
        var vm = Create(test, D(2026, 9, 24));
        vm.CurrentView = CalendarView.Agenda;
        vm.SelectedDate = D(2026, 9, 10);
        Assert.True(vm.Agenda.RowOn(D(2026, 9, 10))!.IsSelected);

        vm.TodayCommand.Execute(null);

        Assert.Equal(D(2026, 9, 24), vm.Agenda.SelectedDate);
        Assert.True(vm.Agenda.RowOn(D(2026, 9, 24))!.IsSelected);
        Assert.False(vm.Agenda.RowOn(D(2026, 9, 10))!.IsSelected);
    }

    [Fact]
    public void 前の今日を選んでいたら日付をまたいだあと週の印も新しい今日へ移る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));
        vm.CurrentView = CalendarView.Week;
        Assert.Equal(D(2026, 9, 24), vm.Week.Days.Single(d => d.IsSelected).Date);

        vm.UpdateNow(new DateTime(2026, 9, 25, 0, 1, 0));

        Assert.Equal(D(2026, 9, 25), vm.Week.Days.Single(d => d.IsSelected).Date);
    }

    [Fact]
    public void 別の日を選んでいたら日付をまたいでも週と一覧の印はその日のまま()
    {
        using var test = TestWorkspace.Create();
        AddEventsOn(test, D(2026, 9, 28), D(2026, 10, 1));
        var vm = Create(test, D(2026, 9, 30));
        vm.CurrentView = CalendarView.Week;
        vm.SelectedDate = D(2026, 9, 28);
        _ = vm.Agenda;

        vm.UpdateNow(new DateTime(2026, 10, 1, 0, 1, 0));

        // 週は作り直されても印が残る
        Assert.Equal(D(2026, 9, 28), vm.Week.Days.Single(d => d.IsSelected).Date);

        // 一覧は今日が変わると作り直される。そのとき選んでいた日の行に印が付く
        // （作りたての一覧が、新しい今日を選んだことにしてはいけない）
        Assert.Equal(D(2026, 9, 28), vm.Agenda.SelectedDate);
        Assert.True(vm.Agenda.RowOn(D(2026, 9, 28))!.IsSelected);
        Assert.False(vm.Agenda.RowOn(D(2026, 10, 1))!.IsSelected);
    }

    [Fact]
    public void 設定で組み直しても週の選択の印は残る()
    {
        using var test = TestWorkspace.Create();
        var settings = Settings(test);
        var vm = Create(test, D(2026, 9, 24), settings);
        vm.CurrentView = CalendarView.Week;
        vm.SelectedDate = D(2026, 9, 22);

        settings.WeekStart = DayOfWeek.Monday;

        Assert.Equal(D(2026, 9, 22), vm.Week.Days.Single(d => d.IsSelected).Date);
    }

    // ------------------------------------------------------------------
    // 項目3: 日付をまたいでも、見出しの「実働 n / 残り n 日」が前日のまま
    // ------------------------------------------------------------------

    [Fact]
    public void 別の日を選んでいても日付をまたいだら見出しを通知し直す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));
        vm.SelectedDate = D(2026, 9, 10);
        var before = vm.WorkingDaySummary;
        var names = Watch(vm);

        vm.UpdateNow(new DateTime(2026, 9, 25, 0, 1, 0));

        Assert.Contains(nameof(MainViewModel.WorkingDaySummary), names);
        Assert.Contains(nameof(MainViewModel.RemainingWorkingDaysText), names);
        Assert.NotEqual(before, vm.WorkingDaySummary);
    }

    [Fact]
    public void 月をまたいだら見ている月に今日が無いので残りを出さない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 30));
        vm.SelectedDate = D(2026, 9, 10);
        Assert.True(vm.HasRemainingWorkingDays);
        var names = Watch(vm);

        vm.UpdateNow(new DateTime(2026, 10, 1, 0, 1, 0));

        // 見ているのは 9 月のまま。今日（10/1）は含まないので、残りは出さない
        Assert.Contains(nameof(MainViewModel.HasRemainingWorkingDays), names);
        Assert.False(vm.HasRemainingWorkingDays);
        Assert.DoesNotContain("残り", vm.WorkingDaySummary);
    }

    [Fact]
    public void 今日を直に差し替えても見出しを通知し直す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));
        var names = Watch(vm);

        vm.Today = D(2026, 9, 25);

        Assert.Contains(nameof(MainViewModel.WorkingDaySummary), names);
    }

    // ------------------------------------------------------------------
    // 項目13: パネルを全部閉じかけて中央が戻ったとき、見出しの書式が戻らない
    // ------------------------------------------------------------------

    [Fact]
    public void 最後のパネルを閉じて中央が戻ったら見出しの年月も戻る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));

        vm.IsMainViewOpen = false;
        vm.IsSidePanelOpen = false;
        Assert.Equal("9月24日", vm.TitleMonth);

        var names = Watch(vm);

        // 3つとも畳むと、中央が自動で戻る（EnsureSomethingShows）
        vm.IsDetailPaneOpen = false;

        Assert.True(vm.IsMainViewOpen);
        Assert.Contains(nameof(MainViewModel.TitleMonth), names);
        Assert.Contains(nameof(MainViewModel.TitleYear), names);
        Assert.Equal("9月", vm.TitleMonth);
    }

    [Fact]
    public void 中央を閉じて右パネルが戻ったときも中央の見出しは畳んだ書式のまま()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));
        vm.IsSidePanelOpen = false;
        vm.IsDetailPaneOpen = false;

        vm.IsMainViewOpen = false;

        Assert.True(vm.IsDetailPaneOpen);
        Assert.Equal("9月24日", vm.TitleMonth);
    }

    // ------------------------------------------------------------------
    // 項目14: 現在時刻の線が最大1分出ない
    // ------------------------------------------------------------------

    [Fact]
    public void 週ビューは別の週へ移って戻った瞬間に現在時刻の線を計算し直す()
    {
        using var test = TestWorkspace.Create();
        var vm = new WeekViewModel(test.Workspace, D(2026, 9, 24), D(2026, 9, 24));
        vm.UpdateNowLine(T(10));
        Assert.True(vm.ShowNowLine);

        vm.GoToNextWeek();
        Assert.False(vm.ShowNowLine);

        vm.GoToToday();
        Assert.True(vm.ShowNowLine);
    }

    [Fact]
    public void 日ビューは別の日へ移って戻った瞬間に現在時刻の線を計算し直す()
    {
        using var test = TestWorkspace.Create();
        var vm = new DayViewModel(test.Workspace, D(2026, 9, 24), D(2026, 9, 24));
        vm.UpdateNowLine(T(10));
        Assert.True(vm.ShowNowLine);

        vm.GoToNextDay();
        Assert.False(vm.ShowNowLine);

        vm.GoToToday();
        Assert.True(vm.ShowNowLine);
    }

    [Fact]
    public void 今日が変わった瞬間に線の出る週が変わる()
    {
        using var test = TestWorkspace.Create();
        var vm = new WeekViewModel(test.Workspace, D(2026, 9, 24), D(2026, 9, 24));
        vm.UpdateNowLine(T(10));

        // 来週の月曜になった。見ている週はまだ先週なので、線は出ない
        vm.Today = D(2026, 9, 28);

        Assert.False(vm.ShowNowLine);
    }

    [Fact]
    public void 設定で作り直した週と日にも次の1分を待たずに線が出る()
    {
        using var test = TestWorkspace.Create();
        var settings = Settings(test);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var vm = new MainViewModel(test.Workspace, D(2026, 9, 24), settings: settings, clock: clock);
        Assert.True(vm.Week.ShowNowLine);
        Assert.True(vm.Day.ShowNowLine);

        var oldWeek = vm.Week;
        settings.WeekStart = DayOfWeek.Monday;

        Assert.NotSame(oldWeek, vm.Week);
        Assert.True(vm.Week.ShowNowLine);
        Assert.True(vm.Day.ShowNowLine);
    }

    // ------------------------------------------------------------------
    // 項目16: 暦日/実働日の数え方が、日ビューだけに効かない
    // ------------------------------------------------------------------

    [Fact]
    public void 日ビューも暦日で数える設定に従う()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.CountInCalendarDays = true;

        // 9/24 から月末（9/30）まで 6 日。右ペインと同じく暦日で数える
        var day = new DayViewModel(test.Workspace, D(2026, 9, 24), D(2026, 9, 24));
        Assert.Equal("月末まで 6日", day.RemainingInMonthText);

        // 暦日なら実働日データは要らない。データの無い月でも出す
        var later = new DayViewModel(test.Workspace, D(2026, 10, 5), D(2026, 9, 24));
        Assert.Equal("月末まで 26日", later.RemainingInMonthText);
    }

    [Fact]
    public void 実働日で数える設定のままなら日ビューは今までどおり()
    {
        using var test = TestWorkspace.Create();

        var day = new DayViewModel(test.Workspace, D(2026, 9, 24), D(2026, 9, 24));

        Assert.Equal("月末まで 4実働日", day.RemainingInMonthText);
    }

    [Fact]
    public void 設定を切り替えたら表示中の日ビューの月末までの文言も変わる()
    {
        using var test = TestWorkspace.Create();
        var settings = Settings(test);
        var vm = Create(test, D(2026, 9, 24), settings);
        vm.CurrentView = CalendarView.Day;
        Assert.Equal("月末まで 4実働日", vm.Day.RemainingInMonthText);
        var names = Watch(vm.Day);

        settings.CountInCalendarDays = true;

        Assert.Contains(nameof(DayViewModel.RemainingInMonthText), names);
        Assert.Equal("月末まで 6日", vm.Day.RemainingInMonthText);
    }

    // ------------------------------------------------------------------
    // 項目12: データが変わっても検索結果が古いまま
    // ------------------------------------------------------------------

    [Fact]
    public void 検索結果を出しているときに予定を足したら検索をやり直す()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議A", Date = D(2026, 9, 24) });
        var vm = Create(test, D(2026, 9, 24));
        vm.SearchText = "会議";
        Assert.Single(vm.SearchResults);

        test.Workspace.AddEvent(new CalendarEvent { Id = "e2", Title = "会議B", Date = D(2026, 9, 25) });

        Assert.Equal(2, vm.SearchResults.Count);
    }

    [Fact]
    public void 検索結果の予定を消したら結果からも消える()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議A", Date = D(2026, 9, 24) });
        test.Workspace.AddEvent(new CalendarEvent { Id = "e2", Title = "会議B", Date = D(2026, 9, 25) });
        var vm = Create(test, D(2026, 9, 24));
        vm.SearchText = "会議";
        Assert.Equal(2, vm.SearchResults.Count);

        test.Workspace.DeleteEvent("e1");

        Assert.Equal("e2", vm.SearchResults.Single().Id);
    }

    [Fact]
    public void 見つからなかった検索も予定を足したら見つかる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));
        vm.SearchText = "出張";
        Assert.Equal("見つかりませんでした", vm.SearchMessage);

        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "出張", Date = D(2026, 9, 24) });

        Assert.Null(vm.SearchMessage);
        Assert.Single(vm.SearchResults);
    }

    [Fact]
    public void 検索していないときはデータが変わっても結果を作らない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));

        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });

        Assert.Empty(vm.SearchResults);
        Assert.Null(vm.SearchMessage);
    }

    // ------------------------------------------------------------------
    // 項目10: 配信元の URL を入れても「配信元から取り込む」が押せないまま
    // ------------------------------------------------------------------

    [Fact]
    public void 配信元を入れたら取り込みのコマンドが押せるようになる()
    {
        using var test = TestWorkspace.Create();
        var settings = Settings(test);
        var vm = Create(test, D(2026, 9, 24), settings);
        Assert.False(vm.FetchWorkingDayFeedCommand.CanExecute(null));

        var raised = 0;
        vm.FetchWorkingDayFeedCommand.CanExecuteChanged += (_, _) => raised++;

        settings.FeedUrl = "https://example.com/feed.json";

        Assert.True(raised > 0);
        Assert.True(vm.FetchWorkingDayFeedCommand.CanExecute(null));
    }

    [Fact]
    public void 配信元を空に戻したら取り込みのコマンドはまた押せなくなる()
    {
        using var test = TestWorkspace.Create();
        var settings = Settings(test);
        settings.FeedUrl = "https://example.com/feed.json";
        var vm = Create(test, D(2026, 9, 24), settings);
        Assert.True(vm.FetchWorkingDayFeedCommand.CanExecute(null));

        var raised = 0;
        vm.FetchWorkingDayFeedCommand.CanExecuteChanged += (_, _) => raised++;

        settings.FeedUrl = string.Empty;

        Assert.True(raised > 0);
        Assert.False(vm.FetchWorkingDayFeedCommand.CanExecute(null));
    }

    [Fact]
    public void 更新の確認と復元の口を後から入れてもコマンドの押せる状態が変わる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24), Settings(test));
        Assert.False(vm.CheckForUpdateCommand.CanExecute(null));
        Assert.False(vm.RestoreCommand.CanExecute(null));

        var update = 0;
        var restore = 0;
        vm.CheckForUpdateCommand.CanExecuteChanged += (_, _) => update++;
        vm.RestoreCommand.CanExecuteChanged += (_, _) => restore++;

        vm.CheckForUpdate = () => Task.CompletedTask;
        vm.RestoreBackup = _ => { };

        Assert.True(update > 0);
        Assert.True(restore > 0);
        Assert.True(vm.CheckForUpdateCommand.CanExecute(null));
        Assert.True(vm.RestoreCommand.CanExecute(null));
    }

    // ------------------------------------------------------------------
    // 項目6: 年の出し方・表示時間帯・暦日/実働日で、組み直しが余分に走る
    // ------------------------------------------------------------------

    [Fact]
    public void 年の出し方を切り替えても他のビューは作り直さない()
    {
        using var test = TestWorkspace.Create();
        var settings = Settings(test);
        var vm = Create(test, D(2026, 9, 24), settings);
        var year = vm.Year;
        var (month, week, day, pane) = (vm.Month, vm.Week, vm.Day, vm.PaneMonth);
        var refreshes = year.RefreshCount;
        var names = Watch(vm);

        year.Layout = year.Layout == YearLayout.Strip ? YearLayout.Grid : YearLayout.Strip;

        Assert.Same(month, vm.Month);
        Assert.Same(week, vm.Week);
        Assert.Same(day, vm.Day);
        Assert.Same(pane, vm.PaneMonth);
        Assert.Same(year, vm.Year);
        Assert.Equal(year.Layout, settings.YearLayout);
        Assert.Equal(refreshes, year.RefreshCount);
        Assert.DoesNotContain(nameof(MainViewModel.Week), names);
        Assert.DoesNotContain(nameof(MainViewModel.Title), names);
    }

    [Fact]
    public void 設定のほうから年の出し方を変えたら作ってある年ビューへ切り替えを伝える()
    {
        using var test = TestWorkspace.Create();
        var settings = Settings(test);
        var vm = Create(test, D(2026, 9, 24), settings);
        var year = vm.Year;
        var week = vm.Week;
        var other = year.Layout == YearLayout.Strip ? YearLayout.Grid : YearLayout.Strip;

        settings.YearLayout = other;

        Assert.Equal(other, year.Layout);
        Assert.Same(year, vm.Year);
        Assert.Same(week, vm.Week);
    }

    [Fact]
    public void 年を作っていないときの出し方の変更は次に作る年ビューが受け取る()
    {
        using var test = TestWorkspace.Create();
        var settings = Settings(test);
        var vm = Create(test, D(2026, 9, 24), settings);
        var other = settings.YearLayout == YearLayout.Strip ? YearLayout.Grid : YearLayout.Strip;

        settings.YearLayout = other;
        vm.CurrentView = CalendarView.Year;

        Assert.Equal(other, vm.Year.Layout);
    }

    [Fact]
    public void 表示時間帯の上端を下端へ押し出す変更で設定の変更通知は1回()
    {
        using var test = TestWorkspace.Create();
        var settings = Settings(test);
        settings.DayEndHour = 18;
        var changed = 0;
        settings.Changed += (_, _) => changed++;

        // 20時を上端にすると、下端 18 時は押し出されて 21 時になる
        settings.DayStartHour = 20;

        Assert.Equal(20, settings.DayStartHour);
        Assert.Equal(21, settings.DayEndHour);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void 下端を上端へ押し込む変更でも設定の変更通知は1回()
    {
        using var test = TestWorkspace.Create();
        var settings = Settings(test);
        settings.DayStartHour = 9;
        var changed = 0;
        settings.Changed += (_, _) => changed++;

        settings.DayEndHour = 6;

        Assert.Equal(5, settings.DayStartHour);
        Assert.Equal(6, settings.DayEndHour);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void 表示時間帯の上端を押し出す変更でビューの組み直しは1回()
    {
        using var test = TestWorkspace.Create();
        var settings = Settings(test);
        settings.DayEndHour = 18;
        var vm = Create(test, D(2026, 9, 24), settings);
        var names = Watch(vm);

        settings.DayStartHour = 20;

        Assert.Equal(1, names.Count(n => n == nameof(MainViewModel.Week)));
    }

    [Fact]
    public void 暦日と実働日の切り替えで画面の引き直しは1回()
    {
        using var test = TestWorkspace.Create();
        var settings = Settings(test);
        var vm = Create(test, D(2026, 9, 24), settings);
        var names = Watch(vm);

        settings.CountInCalendarDays = true;

        // RefreshViews は見出しを1回通知する
        Assert.Equal(1, names.Count(n => n == nameof(MainViewModel.Title)));
        Assert.True(test.Workspace.CountInCalendarDays);
    }

    // ------------------------------------------------------------------
    // 項目11: 年・一覧を初めて出すとき、重い組み立てが2回走る
    // ------------------------------------------------------------------

    [Fact]
    public void データが変わったあとで初めて年を出しても組み立ては1回()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));

        // 出していないビューは作り直さず、印だけ付く
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });

        vm.CurrentView = CalendarView.Year;

        Assert.Equal(1, vm.Year.RefreshCount);
    }

    [Fact]
    public void データが変わったあとで初めて一覧を出しても組み立ては1回()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));

        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });

        vm.CurrentView = CalendarView.Agenda;

        Assert.Equal(1, vm.Agenda.RefreshCount);
    }

    [Fact]
    public void 選んでいる日が別の年度にあるとき年の組み立ては1回()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));
        vm.SelectedDate = D(2027, 5, 1);

        vm.CurrentView = CalendarView.Year;

        Assert.Equal(2027, vm.Year.FiscalYear);
        Assert.Equal(1, vm.Year.RefreshCount);
    }

    [Fact]
    public void 作ってある年と一覧は印が立っていれば次に出すとき1回だけ組み直す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));
        vm.CurrentView = CalendarView.Year;
        var year = vm.Year;
        vm.CurrentView = CalendarView.Month;
        var before = year.RefreshCount;

        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });
        Assert.Equal(before, year.RefreshCount);

        vm.CurrentView = CalendarView.Year;

        Assert.Equal(before + 1, year.RefreshCount);
    }

    // ------------------------------------------------------------------
    // 項目15: 日付またぎで作り直した一覧の、スクロールの依頼が届かない
    // ------------------------------------------------------------------

    [Fact]
    public void 聞いている画面が無いときの位置合わせは控えて画面が引き取る()
    {
        using var test = TestWorkspace.Create();
        var agenda = new AgendaViewModel(test.Workspace, D(2026, 9, 24), DefaultCalendarSources.Instance);

        agenda.GoTo(D(2026, 9, 10));

        Assert.Equal(D(2026, 9, 10), agenda.TakePendingScroll());

        // 引き取ったら消える
        Assert.Null(agenda.TakePendingScroll());
    }

    [Fact]
    public void 聞いている画面があれば位置合わせは直に届き控えは残らない()
    {
        using var test = TestWorkspace.Create();
        var agenda = new AgendaViewModel(test.Workspace, D(2026, 9, 24), DefaultCalendarSources.Instance);
        var requested = new List<DateOnly>();
        agenda.ScrollRequested += (_, date) => requested.Add(date);

        agenda.GoTo(D(2026, 9, 10));

        Assert.Equal([D(2026, 9, 10)], requested);
        Assert.Null(agenda.TakePendingScroll());
    }

    [Fact]
    public void 後から聞き始めた画面にも先に出た依頼を引き渡せる()
    {
        using var test = TestWorkspace.Create();
        var agenda = new AgendaViewModel(test.Workspace, D(2026, 9, 24), DefaultCalendarSources.Instance);
        agenda.GoTo(D(2026, 9, 10));
        agenda.GoTo(D(2026, 9, 12));

        // 最後の依頼だけが生きている
        Assert.Equal(D(2026, 9, 12), agenda.TakePendingScroll());
    }

    [Fact]
    public void 日付をまたいで作り直した一覧は選んでいた日の位置合わせを控えている()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 30));
        vm.CurrentView = CalendarView.Agenda;
        vm.SelectedDate = D(2026, 9, 10);

        var before = vm.Agenda;
        vm.UpdateNow(new DateTime(2026, 10, 1, 0, 1, 0));

        Assert.NotSame(before, vm.Agenda);

        // 画面はまだ新しい実体を受け取っていない。受け取ったとき（DataContextChanged）に動かす
        Assert.Equal(D(2026, 9, 10), vm.Agenda.TakePendingScroll());
    }

    [Fact]
    public void 前の今日を選んでいたなら作り直した一覧は新しい今日へ送る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 30));
        vm.CurrentView = CalendarView.Agenda;

        vm.UpdateNow(new DateTime(2026, 10, 1, 0, 1, 0));

        Assert.Equal(D(2026, 10, 1), vm.Agenda.TakePendingScroll());
    }

    // ------------------------------------------------------------------
    // 項目7: 実働日計算パネルを開いたまま実働日データを取り込むと、古いデータで計算する
    // ------------------------------------------------------------------

    private static WorkingDayCalendar SeptemberDays()
    {
        var days = Enumerable.Range(1, 30)
            .Select(d => D(2026, 9, d))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .ToArray();

        return WorkingDayCalendar.Create(days, D(2026, 9, 1), D(2026, 9, 30), [], null, null);
    }

    [Fact]
    public void 計算パネルを開いたまま実働日を取り込んだら最新のデータで計算する()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var editors = new FakeEditorPresenter();
        var vm = new MainViewModel(test.Workspace, D(2026, 9, 24), editors: editors);
        vm.OpenWorkingDayCalculatorCommand.Execute(null);
        var calculator = editors.LastCalculator!;
        calculator.SetRange(D(2026, 9, 7), D(2026, 9, 11));
        Assert.False(calculator.HasData);
        Assert.Null(calculator.CoverageText);
        Assert.False(calculator.HasRangeResult);
        var names = Watch(calculator);

        test.Workspace.WorkingDayStore.Save(SeptemberDays());
        test.Workspace.ReloadWorkingDays();

        Assert.True(calculator.HasData);
        Assert.NotNull(calculator.CoverageText);
        Assert.True(calculator.HasRangeResult);
        Assert.Equal(5, calculator.RangeCount);
        Assert.Contains(nameof(WorkdayCalculatorViewModel.HasData), names);
        Assert.Contains(nameof(WorkdayCalculatorViewModel.CoverageText), names);
        Assert.Contains(nameof(WorkdayCalculatorViewModel.RangeResultText), names);
        Assert.Contains(nameof(WorkdayCalculatorViewModel.ArrivalResultText), names);
    }

    [Fact]
    public void 計算パネルの工程逆算も取り込んだ最新のデータで計算し直す()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var editors = new FakeEditorPresenter();
        var vm = new MainViewModel(test.Workspace, D(2026, 9, 24), editors: editors);
        vm.OpenWorkingDayCalculatorCommand.Execute(null);
        var calculator = editors.LastCalculator!;
        calculator.PlanDueDate = D(2026, 9, 24);
        Assert.All(calculator.PlanRows, row => Assert.Null(row.Date));

        test.Workspace.WorkingDayStore.Save(SeptemberDays());
        test.Workspace.ReloadWorkingDays();

        Assert.Contains(calculator.PlanRows, row => row.Date is not null);
    }

    [Fact]
    public void 計算パネルを閉じたあとはデータが変わっても何も追わない()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var editors = new FakeEditorPresenter();
        var vm = new MainViewModel(test.Workspace, D(2026, 9, 24), editors: editors);
        vm.OpenWorkingDayCalculatorCommand.Execute(null);
        var calculator = editors.LastCalculator!;
        calculator.NotifyClosed();
        var names = Watch(calculator);

        test.Workspace.WorkingDayStore.Save(SeptemberDays());
        test.Workspace.ReloadWorkingDays();

        Assert.Empty(names);
    }
}
