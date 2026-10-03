using System.Net;
using System.Text;
using Kado.Core.Import;
using Kado.Data.Models;
using Kado.Google.Sync;
using Kado.Presentation.Settings;
using Kado.Presentation.Sync;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 右パネルの月カレンダー（<c>MainViewModel.PaneMonth</c>）が、データの変わったあとも
/// 最新のままでいること。
/// <para>
/// 予定があるのに何も出ない、という形で実機から返ってきた。原因は2つ重なっていた。
/// 起動のあとに届くデータ（実働日の配信・同期）を受けても、データ変更時の引き直しが
/// 中央の月・選択日・ミニ月暦だけを対象にしていて、右パネルの月カレンダーが入って
/// いなかったこと。設定が変わって組み直すときも、作り直した新しい月カレンダーを画面へ
/// 通知していなかったこと。
/// </para>
/// </summary>
public class PaneMonthRefreshTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static CalendarEvent Event(string id, DateOnly date, string? calendarId = null) =>
        new() { Id = id, Title = "会議", Date = date, CalendarId = calendarId };

    /// <summary>月カレンダーのマスに並んでいる予定の数（前後の月のマスも含む）。</summary>
    private static int EventCount(MonthViewModel month) => month.Cells.Sum(c => c.AllEvents.Count);

    /// <summary>通知された名前を集める。画面はこの通知でしか値の変化を知らない。</summary>
    private static List<string?> WatchRaised(MonthViewModel month)
    {
        var raised = new List<string?>();
        month.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }

    /// <summary>右パネルの月カレンダーを開いた状態の設定。</summary>
    private static AppSettings OpenedSettings(TestWorkspace test) =>
        new(test.Workspace.Settings) { IsPaneCalendarCollapsed = false };

    // ------------------------------------------------------------------
    // 起動直後
    // ------------------------------------------------------------------

    [Fact]
    public void 起動直後の右パネルの月に既存の予定が出ていて案内は出ない()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        test.Workspace.AddEvent(Event("e1", Today));

        var vm = new MainViewModel(test.Workspace, Today, settings: OpenedSettings(test));

        Assert.Equal(1, EventCount(vm.PaneMonth));
        Assert.False(vm.PaneMonth.ShowsEmptyGuide);
    }

    [Fact]
    public void 起動直後の右パネルの月は設定を持たない組み立て方でも予定が出る()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        test.Workspace.AddEvent(Event("e1", Today));

        var vm = new MainViewModel(test.Workspace, Today);

        Assert.Equal(1, EventCount(vm.PaneMonth));
        Assert.False(vm.PaneMonth.ShowsEmptyGuide);
    }

    /// <summary>
    /// 起動のあとに届く実働日データ（配信元の自動取得は既定でオン）。
    /// <para>
    /// 右パネルの月カレンダーは組み立てた時点では「予定なし・実働日データなし」で、
    /// 道案内まで出ていた。届いたあとに引き直さないと、そのまま変わらなかった。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 起動のあとに届いた実働日データが右パネルの月に反映される()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var gate = new TaskCompletionSource();
        var handler = new GatedFeedHandler(gate.Task);

        var settings = new AppSettings(test.Workspace.Settings)
        {
            FeedUrl = "https://example.com/feed.json",
            FeedAuto = true,
            IsPaneCalendarCollapsed = false,
        };

        var vm = new MainViewModel(
            test.Workspace, Today, settings: settings, feed: new WorkdayFeedClient(new HttpClient(handler)));

        // 届く前は、実働日データが無い月として出ている
        Assert.False(vm.PaneMonth.HasFullWorkingDayData);
        Assert.True(vm.PaneMonth.ShowsEmptyGuide);

        // 案内の出し入れは画面が通知で知る。値だけ変わっても画面には届かない
        var raised = WatchRaised(vm.PaneMonth);

        gate.SetResult();
        for (var i = 0; i < 200 && settings.FeedCheckedOn is null; i++) await Task.Delay(10);

        Assert.True(vm.PaneMonth.HasFullWorkingDayData);
        Assert.False(vm.PaneMonth.ShowsEmptyGuide);
        Assert.Contains(nameof(MonthViewModel.ShowsEmptyGuide), raised);
    }

    /// <summary>開けと言われるまで返事を返さない配信元。起動よりあとにデータが届く形を作る。</summary>
    private sealed class GatedFeedHandler(Task gate) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await gate.ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"app":"inaCalendar","type":"workingdays","updatedAt":"2026-09-20",
                     "workingDays":["2026-09-24"],
                     "dataStart":"2026-09-01","dataEnd":"2026-09-30"}
                    """,
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    // ------------------------------------------------------------------
    // データが変わったとき
    // ------------------------------------------------------------------

    [Fact]
    public void 予定を足すと右パネルの月のマスに出る()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var vm = new MainViewModel(test.Workspace, Today, settings: OpenedSettings(test));

        Assert.Equal(0, EventCount(vm.PaneMonth));
        Assert.True(vm.PaneMonth.ShowsEmptyGuide);

        test.Workspace.AddEvent(Event("e1", D(2026, 9, 10)));

        var cell = vm.PaneMonth.Cells.Single(c => c.Date == D(2026, 9, 10));
        Assert.Single(cell.AllEvents);
        Assert.False(vm.PaneMonth.ShowsEmptyGuide);
    }

    [Fact]
    public void 中央が週ビューでも右パネルの月は最新になる()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var vm = new MainViewModel(test.Workspace, Today, settings: OpenedSettings(test));
        vm.SwitchViewCommand.Execute(CalendarView.Week);

        test.Workspace.AddEvent(Event("e1", D(2026, 9, 10)));

        Assert.Equal(1, EventCount(vm.PaneMonth));
    }

    [Fact]
    public void 予定を消すと右パネルの月からも消える()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        test.Workspace.AddEvent(Event("e1", D(2026, 9, 10)));
        var vm = new MainViewModel(test.Workspace, Today, settings: OpenedSettings(test));

        Assert.Equal(1, EventCount(vm.PaneMonth));

        test.Workspace.DeleteEvent("e1");

        Assert.Equal(0, EventCount(vm.PaneMonth));
    }

    [Fact]
    public void 実働日データを取り込むと右パネルの月の案内が消える()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var vm = new MainViewModel(test.Workspace, Today, settings: OpenedSettings(test));

        Assert.True(vm.PaneMonth.ShowsEmptyGuide);

        var raised = WatchRaised(vm.PaneMonth);

        var days = Enumerable.Range(1, 30)
            .Select(d => D(2026, 9, d))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .ToArray();

        test.Workspace.ApplyWorkingDays(new ImportResult(
            "1", D(2026, 9, 1), D(2026, 9, 30), null, null, days, [], []));

        Assert.True(vm.PaneMonth.HasFullWorkingDayData);
        Assert.False(vm.PaneMonth.ShowsEmptyGuide);
        Assert.Contains(nameof(MonthViewModel.ShowsEmptyGuide), raised);

        // マスの実働日の印も、引き直されて付いている
        Assert.Contains(vm.PaneMonth.Cells, c => c.IsCurrentMonth && c.IsWorkingDayLit);
    }

    // ------------------------------------------------------------------
    // 畳んでいるとき
    // ------------------------------------------------------------------

    [Fact]
    public void 畳んでいるあいだは引き直さず開いたときに最新にする()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);

        // 既定は畳んだ状態
        var settings = new AppSettings(test.Workspace.Settings);
        var vm = new MainViewModel(test.Workspace, Today, settings: settings);
        Assert.True(vm.IsPaneCalendarCollapsed);

        var before = vm.PaneMonth.Cells;
        test.Workspace.AddEvent(Event("e1", D(2026, 9, 10)));

        // 誰も見ていないので、読み直しは溜めておく
        Assert.Same(before, vm.PaneMonth.Cells);

        vm.IsPaneCalendarCollapsed = false;

        Assert.Equal(1, EventCount(vm.PaneMonth));
        Assert.False(vm.PaneMonth.ShowsEmptyGuide);
    }

    [Fact]
    public void 設定の側から開かれても最新になる()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var settings = new AppSettings(test.Workspace.Settings);
        var vm = new MainViewModel(test.Workspace, Today, settings: settings);

        test.Workspace.AddEvent(Event("e1", D(2026, 9, 10)));
        Assert.Equal(0, EventCount(vm.PaneMonth));

        settings.IsPaneCalendarCollapsed = false;

        Assert.Equal(1, EventCount(vm.PaneMonth));
    }

    [Fact]
    public void 右パネルごと閉じていたあとに開いたときも最新になる()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var vm = new MainViewModel(test.Workspace, Today, settings: OpenedSettings(test));

        vm.IsDetailPaneOpen = false;
        test.Workspace.AddEvent(Event("e1", D(2026, 9, 10)));
        Assert.Equal(0, EventCount(vm.PaneMonth));

        vm.IsDetailPaneOpen = true;

        Assert.Equal(1, EventCount(vm.PaneMonth));
    }

    // ------------------------------------------------------------------
    // 左パネルでカレンダーの表示を切り替えたとき
    // ------------------------------------------------------------------

    [Fact]
    public void カレンダーの表示を切り替えると右パネルの月に反映される()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var work = test.Workspace.CreateCalendar("仕事");
        test.Workspace.AddEvent(Event("e1", D(2026, 9, 10), work.Id));

        var vm = new MainViewModel(test.Workspace, Today, settings: OpenedSettings(test));
        Assert.Equal(1, EventCount(vm.PaneMonth));

        var item = vm.SourceLists.Calendars.Single(c => c.Id == work.Id);

        item.IsVisible = false;
        Assert.Equal(0, EventCount(vm.PaneMonth));

        item.IsVisible = true;
        Assert.Equal(1, EventCount(vm.PaneMonth));
    }

    [Fact]
    public void 畳んでいるあいだに表示を切り替えても開いたときに反映される()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var work = test.Workspace.CreateCalendar("仕事");
        test.Workspace.AddEvent(Event("e1", D(2026, 9, 10), work.Id));

        var vm = new MainViewModel(test.Workspace, Today, settings: new AppSettings(test.Workspace.Settings));
        Assert.Equal(1, EventCount(vm.PaneMonth));

        vm.SourceLists.Calendars.Single(c => c.Id == work.Id).IsVisible = false;
        vm.IsPaneCalendarCollapsed = false;

        Assert.Equal(0, EventCount(vm.PaneMonth));
    }

    /// <summary>
    /// 一覧ビューも同じ絞り込みを見る。以前はここが引き直されず、切り替えのあとに
    /// 一覧へ移ると、外したカレンダーの予定が残っていた。
    /// </summary>
    [Fact]
    public void カレンダーの表示を切り替えると一覧ビューにも反映される()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var work = test.Workspace.CreateCalendar("仕事");
        test.Workspace.AddEvent(Event("e1", D(2026, 9, 24), work.Id));

        var vm = new MainViewModel(test.Workspace, Today);
        vm.SwitchViewCommand.Execute(CalendarView.Agenda);
        Assert.Single(vm.Agenda.RowOn(Today)!.Events);

        vm.SwitchViewCommand.Execute(CalendarView.Month);
        vm.SourceLists.Calendars.Single(c => c.Id == work.Id).IsVisible = false;
        vm.SwitchViewCommand.Execute(CalendarView.Agenda);

        Assert.True(vm.Agenda.RowOn(Today) is null || vm.Agenda.RowOn(Today)!.Events.Count == 0);
    }

    // ------------------------------------------------------------------
    // 同期のあと
    // ------------------------------------------------------------------

    [Fact]
    public async Task 同期で予定が入ると右パネルの月に出る()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var vm = new MainViewModel(
            test.Workspace, Today, settings: OpenedSettings(test),
            google: new FakeGoogle(new SyncReport { CreatedLocal = 1 }));

        // 同期は別の接続から書く。こちらの workspace を通らないので、通知は同期の
        // 終わりでしか来ない
        test.Workspace.Events.Upsert(Event("e1", D(2026, 9, 10)));
        Assert.Equal(0, EventCount(vm.PaneMonth));

        await vm.Sync.SyncAsync();

        Assert.Equal(1, EventCount(vm.PaneMonth));
    }

    private sealed class FakeGoogle(SyncReport? report) : IGoogleSync
    {
        public bool IsConnected { get; set; } = true;

        public bool CanConnect { get; set; } = true;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> DisconnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(report);

        public bool HasDriveAttachmentScope => false;

        public Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public GoogleDriveApi CreateDriveApi() => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------
    // 日付が変わったとき
    // ------------------------------------------------------------------

    [Fact]
    public void 日付が変わると右パネルの月の今日の印も移る()
    {
        using var test = TestWorkspace.Create();
        var vm = new MainViewModel(test.Workspace, Today, settings: OpenedSettings(test));

        Assert.True(vm.PaneMonth.Cells.Single(c => c.Date == D(2026, 9, 24)).IsToday);

        vm.UpdateNow(new DateTime(2026, 9, 25, 0, 1, 0));

        Assert.False(vm.PaneMonth.Cells.Single(c => c.Date == D(2026, 9, 24)).IsToday);
        Assert.True(vm.PaneMonth.Cells.Single(c => c.Date == D(2026, 9, 25)).IsToday);
    }

    /// <summary>年と一覧は「今日」を作るときに受け取ったきり持つ。日をまたいだら作り直す。</summary>
    [Fact]
    public void 日付が変わると作ってある一覧の今日の印も移る()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        test.Workspace.AddEvent(Event("e1", D(2026, 9, 24)));
        test.Workspace.AddEvent(Event("e2", D(2026, 9, 25)));

        var vm = new MainViewModel(test.Workspace, Today);
        Assert.True(vm.Agenda.RowOn(D(2026, 9, 24))!.IsToday);

        vm.UpdateNow(new DateTime(2026, 9, 25, 0, 1, 0));

        Assert.False(vm.Agenda.RowOn(D(2026, 9, 24))!.IsToday);
        Assert.True(vm.Agenda.RowOn(D(2026, 9, 25))!.IsToday);
    }

    // ------------------------------------------------------------------
    // 設定が変わって組み直したとき
    // ------------------------------------------------------------------

    /// <summary>
    /// 組み直すと右パネルの月カレンダーも新しくなる。画面は
    /// <c>PaneMonth</c> の通知でそちらへ付け替わる。通知が無いと、画面は捨てたほうを
    /// 持ち続け、以後の選択や月送りが届かなくなる。
    /// </summary>
    [Fact]
    public void 設定で組み直すと右パネルの月の差し替えが通知される()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var settings = OpenedSettings(test);
        var vm = new MainViewModel(test.Workspace, Today, settings: settings);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var before = vm.PaneMonth;

        settings.WeekStart = settings.WeekStart == DayOfWeek.Monday ? DayOfWeek.Sunday : DayOfWeek.Monday;

        Assert.NotSame(before, vm.PaneMonth);
        Assert.Contains(nameof(MainViewModel.PaneMonth), raised);
    }

    [Fact]
    public void 設定で組み直しても右パネルの月は自分で送った月のまま()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        test.Workspace.AddEvent(Event("e1", D(2026, 10, 5)));
        var settings = OpenedSettings(test);
        var vm = new MainViewModel(test.Workspace, Today, settings: settings);

        vm.PaneMonthNextCommand.Execute(null);
        Assert.Equal(10, vm.PaneMonth.Month.Month);

        settings.WeekStart = settings.WeekStart == DayOfWeek.Monday ? DayOfWeek.Sunday : DayOfWeek.Monday;

        Assert.Equal(10, vm.PaneMonth.Month.Month);
        Assert.Equal("10月", vm.PaneTitleMonth);
        Assert.Equal(1, EventCount(vm.PaneMonth));
    }
}
