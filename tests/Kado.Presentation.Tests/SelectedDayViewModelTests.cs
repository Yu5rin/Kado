using Kado.Data.Models;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

public class SelectedDayViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static SelectedDayViewModel Create(TestWorkspace test, DateOnly date) =>
        new(test.Workspace, date, today: D(2026, 9, 24));

    [Fact]
    public void 見出しに曜日が付く()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 24));

        Assert.Equal("9月24日（木）", vm.Title);
    }

    [Fact]
    public void 選択日ヘッダには実働日の通し番号を出す()
    {
        using var test = TestWorkspace.Create();

        // 月ビューのセルには出さないが、ここは出す場所（要件書 4.3）。
        // 9/24 は三連休明けで 15 実働日目
        Assert.Equal("実働 15日目", Create(test, D(2026, 9, 24)).WorkingDayLabel);
    }

    [Fact]
    public void 非稼働日には通し番号が出ない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 9, 21));   // 敬老の日

        Assert.Null(vm.WorkingDayLabel);
        Assert.True(vm.IsNonWorkingDay);
    }

    [Fact]
    public void データ範囲外は非稼働とは言い切らない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test, D(2026, 10, 3));   // 土曜だが範囲外

        Assert.Null(vm.WorkingDayLabel);
        // 判断できないだけで「休み」ではない
        Assert.False(vm.IsNonWorkingDay);
    }

    [Fact]
    public void 予定とタスクを同時に持つ()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });
        ws.AddTask(new TaskItem { Id = "t1", Title = "提出", Due = D(2026, 9, 24) });
        ws.AddTask(new TaskItem { Id = "t2", Title = "済んだ", Due = D(2026, 9, 24), IsDone = true });

        var vm = Create(test, D(2026, 9, 24));

        Assert.Single(vm.Events);
        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal(1, vm.DoneTaskCount);
    }

    [Fact]
    public void タスクに期限の表示が付く()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "提出", Due = D(2026, 9, 25) });

        var vm = Create(test, D(2026, 9, 25));

        var task = Assert.Single(vm.Tasks);
        Assert.Equal("残り 1実働日", task.DueText);
        Assert.Equal(DueEmphasis.Normal, task.Emphasis);
    }

    [Fact]
    public void 期限当日と超過が区別される()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(new TaskItem { Id = "today", Title = "今日まで", Due = D(2026, 9, 24) });
        ws.AddTask(new TaskItem { Id = "late", Title = "遅れ", Due = D(2026, 9, 18) });

        // 右ペインは未完了のタスクをまとめて出すので、どちらの日でも両方並ぶ
        var tasks = Create(test, D(2026, 9, 24)).Tasks;

        Assert.Equal(DueEmphasis.Today, tasks.Single(t => t.Id == "today").Emphasis);
        Assert.Equal(DueEmphasis.Overdue, tasks.Single(t => t.Id == "late").Emphasis);
    }

    [Fact]
    public void 実働日データが無い期間は暦日である旨を補足する()
    {
        using var test = TestWorkspace.Create();
        // 10月は登録範囲の外
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "来月", Due = D(2026, 10, 5) });

        var task = Assert.Single(Create(test, D(2026, 10, 5)).Tasks);

        Assert.Equal("残り 11日", task.DueText);
        Assert.Contains("実働日データが未登録", task.DueTooltip);
    }

    [Fact]
    public void 非稼働日へ寄せたことを補足する()
    {
        using var test = TestWorkspace.Create();
        // 9/27 は日曜。直前の実働日 9/25 に寄る
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "日曜期限", Due = D(2026, 9, 27) });

        var task = Assert.Single(Create(test, D(2026, 9, 27)).Tasks);

        Assert.Equal("残り 1実働日（9/25まで）", task.DueText);
        Assert.Contains("直前の実働日", task.DueTooltip);
    }

    [Fact]
    public void マイルストーンが出る()
    {
        using var test = TestWorkspace.Create(withMilestones: true);

        Assert.Equal("仕様期限", Assert.Single(Create(test, D(2026, 9, 14)).Milestones).Name);
        Assert.Empty(Create(test, D(2026, 9, 15)).Milestones);
    }

    [Fact]
    public void 日を変えると中身が入れ替わる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });

        var vm = Create(test, D(2026, 9, 24));
        Assert.Single(vm.Events);

        vm.Date = D(2026, 9, 25);

        Assert.Empty(vm.Events);
        Assert.Equal("9月25日（金）", vm.Title);
        Assert.Equal("実働 16日目", vm.WorkingDayLabel);
    }

    [Fact]
    public void 先の期限のタスクも並ぶ()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(new TaskItem { Id = "t1", Title = "今日まで", Due = D(2026, 9, 24) });
        ws.AddTask(new TaskItem { Id = "t2", Title = "来月", Due = D(2026, 10, 1) });
        ws.AddTask(new TaskItem { Id = "t3", Title = "年度末", Due = D(2027, 3, 31) });

        var vm = Create(test, D(2026, 9, 24));

        // その日のぶんだけだと、今日やることは分かっても段取りが組めない
        Assert.Equal(["今日まで", "来月", "年度末"], vm.Tasks.Select(t => t.Title));
        Assert.Equal("3 / 3", vm.TaskCountText);
    }

    [Fact]
    public void 完了はその日のぶんだけ添える()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(new TaskItem { Id = "t1", Title = "残り", Due = D(2026, 9, 25) });
        ws.AddTask(new TaskItem { Id = "t2", Title = "今日片付けた", Due = D(2026, 9, 24), IsDone = true });
        ws.AddTask(new TaskItem { Id = "t3", Title = "前に片付けた", Due = D(2026, 9, 10), IsDone = true });

        var vm = Create(test, D(2026, 9, 24));

        // 過去の完了が積み上がると読めない。並びは期限の近い順なので、
        // 今日片付けたぶん（9/24）が 9/25 のものより上に来る
        Assert.Equal(["今日片付けた", "残り"], vm.Tasks.Select(t => t.Title));
        Assert.Equal("1 / 2", vm.TaskCountText);
    }

    [Fact]
    public void 期限の無い未完了タスクは右ペインのタスク節には出さない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "いつかやる" });

        Assert.Empty(Create(test, D(2026, 9, 24)).Tasks);
    }

    // ------------------------------------------------------------------
    // 完了したタスクは、完了した日に出す
    // ------------------------------------------------------------------

    /// <summary>その日の昼（端末のローカル）に完了した、という時刻。</summary>
    private static DateTimeOffset LocalNoon(int y, int m, int d) =>
        new(new DateTime(y, m, d, 12, 0, 0, DateTimeKind.Local));

    private static TaskItem Done(string id, DateOnly? due, DateTimeOffset? completedAt) => new()
    {
        Id = id, Title = $"済み{id}", Due = due, IsDone = true, CompletedAt = completedAt,
    };

    [Fact]
    public void 完了したタスクは完了した日に出て期限日には出ない()
    {
        using var test = TestWorkspace.Create();

        // 期限は 9/30。9/24 に済ませた（前倒し）
        test.Workspace.AddTask(Done("t1", D(2026, 9, 30), LocalNoon(2026, 9, 24)));

        var completedDay = Create(test, D(2026, 9, 24));
        var row = Assert.Single(completedDay.Tasks);
        Assert.True(row.IsDone);
        Assert.False(row.IsFaint);
        Assert.Equal("4実働日 早く完了", row.DoneText);
        Assert.Equal("完了", row.DayLabel);

        // 期限日を選んでも出てこない
        Assert.Empty(Create(test, D(2026, 9, 30)).Tasks);
    }

    [Fact]
    public void 期限どおりの完了はその日に出る()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(Done("t1", D(2026, 9, 24), LocalNoon(2026, 9, 24)));

        var vm = Create(test, D(2026, 9, 24));

        Assert.Equal("期限どおり完了", Assert.Single(vm.Tasks).DoneText);
        Assert.Equal("0 / 1", vm.TaskCountText);
    }

    [Fact]
    public void 遅れて完了したものは期限日に薄く残り完了日が添えられる()
    {
        using var test = TestWorkspace.Create();

        // 9/24 期限を 9/28 に済ませた
        test.Workspace.AddTask(Done("t1", D(2026, 9, 24), LocalNoon(2026, 9, 28)));

        var dueDay = Assert.Single(Create(test, D(2026, 9, 24)).Tasks);
        Assert.True(dueDay.IsFaint);
        Assert.Equal("9/28 完了", dueDay.DoneText);
        Assert.Equal("期限", dueDay.DayLabel);
        // 薄い跡は静かに出す。遅れの赤は完了した日のほう
        Assert.False(dueDay.IsLate);

        var completedDay = Assert.Single(Create(test, D(2026, 9, 28)).Tasks);
        Assert.False(completedDay.IsFaint);
        Assert.Equal("期限 9/24・2実働日遅れ", completedDay.DoneText);
        Assert.True(completedDay.IsLate);
    }

    [Fact]
    public void 期限日に残る薄い跡は件数に数えない()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(Done("t1", D(2026, 9, 24), LocalNoon(2026, 9, 28)));   // 9/24 では薄い跡
        ws.AddTask(new TaskItem { Id = "t2", Title = "残り", Due = D(2026, 9, 25) });

        var vm = Create(test, D(2026, 9, 24));

        Assert.Equal(2, vm.Tasks.Count);
        // 分母にも分子にも入れない（別の日に片付けたもの）
        Assert.Equal("1 / 1", vm.TaskCountText);
        Assert.Equal(0, vm.DoneTaskCount);
    }

    [Fact]
    public void 完了日時が無い完了タスクは今までどおり期限日に出る()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(Done("t1", D(2026, 9, 24), completedAt: null));

        var row = Assert.Single(Create(test, D(2026, 9, 24)).Tasks);

        Assert.True(row.IsDone);
        Assert.Null(row.DoneText);
        Assert.Equal("期限", row.DayLabel);
        Assert.Empty(Create(test, D(2026, 9, 25)).Tasks);
    }

    [Fact]
    public void 期限の無い完了タスクは完了日に出る()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(Done("t1", due: null, LocalNoon(2026, 9, 24)));

        var vm = Create(test, D(2026, 9, 24));
        Assert.True(Assert.Single(vm.Tasks).IsDone);
        Assert.Equal("0 / 1", vm.TaskCountText);

        Assert.Empty(Create(test, D(2026, 9, 25)).Tasks);
    }

    [Fact]
    public void 未完了は今までどおり期限の日にかかわらず並ぶ()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(new TaskItem { Id = "t1", Title = "遅れ", Due = D(2026, 9, 10) });
        ws.AddTask(new TaskItem { Id = "t2", Title = "先", Due = D(2026, 10, 30) });

        Assert.Equal(["遅れ", "先"], Create(test, D(2026, 9, 24)).Tasks.Select(t => t.Title));
    }

    [Fact]
    public void 遅れの日数は暦日で数える設定に従う()
    {
        using var test = TestWorkspace.Create();

        // 9/24（木）期限を 9/28（月）に。実働日なら2、暦日なら4
        test.Workspace.AddTask(Done("t1", D(2026, 9, 24), LocalNoon(2026, 9, 28)));
        test.Workspace.CountInCalendarDays = true;

        Assert.Equal("期限 9/24・4日遅れ", Assert.Single(Create(test, D(2026, 9, 28)).Tasks).DoneText);
    }

    // ------------------------------------------------------------------
    // 期限なしタスクの節（項目1）
    //
    // 「期限を付ける」を外したタスクは Tasks から落ちるだけで、保存はされている。
    // 二度と見えず編集も削除もできなかった分を、別の節として出す
    // ------------------------------------------------------------------

    [Fact]
    public void 期限の無いタスクはNoDueTasksに出る()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "いつかやる" });

        var vm = Create(test, D(2026, 9, 24));

        var row = Assert.Single(vm.NoDueTasks);
        Assert.Equal("t1", row.Id);
        Assert.True(vm.ShowsNoDueTasks);
        Assert.Equal(1, vm.NoDueTaskCount);
    }

    [Fact]
    public void 期限の無い完了済みタスクはNoDueTasksにも出さない()
    {
        using var test = TestWorkspace.Create();
        // 期限が無いまま完了したものは、いつ片付けたかを表示する場所が無い
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "片付けた", IsDone = true });

        var vm = Create(test, D(2026, 9, 24));

        Assert.Empty(vm.NoDueTasks);
        Assert.False(vm.ShowsNoDueTasks);
    }

    [Fact]
    public void 期限のあるタスクはNoDueTasksに出さない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "提出", Due = D(2026, 9, 25) });

        Assert.Empty(Create(test, D(2026, 9, 24)).NoDueTasks);
    }

    [Fact]
    public void 期限なしタスクは登録順に並ぶ()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        // わざと五十音とは逆の順で足す。並びは題名ではなく登録順のはず
        ws.AddTask(new TaskItem { Id = "t1", Title = "び" });
        ws.AddTask(new TaskItem { Id = "t2", Title = "あ" });

        var vm = Create(test, D(2026, 9, 24));

        Assert.Equal(["び", "あ"], vm.NoDueTasks.Select(t => t.Title));
    }

    [Fact]
    public void 日を変えても期限なしタスクの並びは変わらない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "いつかやる" });

        var vm = Create(test, D(2026, 9, 24));
        Assert.Single(vm.NoDueTasks);

        vm.Date = D(2026, 9, 25);

        // 選択日に関係なく見えている
        Assert.Single(vm.NoDueTasks);
    }

    [Fact]
    public void 日を変えてもタスクの並びは変わらない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "来月", Due = D(2026, 10, 1) });

        var vm = Create(test, D(2026, 9, 24));
        Assert.Single(vm.Tasks);

        vm.Date = D(2026, 9, 25);

        // 未完了のタスクは選択日に関係なく見えている
        Assert.Single(vm.Tasks);
    }

    [Fact]
    public void 期限の近い順に並ぶ()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(new TaskItem { Id = "t1", Title = "年度末", Due = D(2027, 3, 31) });
        ws.AddTask(new TaskItem { Id = "t2", Title = "遅れ", Due = D(2026, 9, 18) });
        ws.AddTask(new TaskItem { Id = "t3", Title = "来月", Due = D(2026, 10, 1) });
        ws.AddTask(new TaskItem { Id = "t4", Title = "今日まで", Due = D(2026, 9, 24) });

        var vm = Create(test, D(2026, 9, 24));

        // 遅れているものが一番上。先の期限ほど下
        Assert.Equal(["遅れ", "今日まで", "来月", "年度末"], vm.Tasks.Select(t => t.Title));
    }

    [Fact]
    public void 同じ期限日に足したタスクは下に付く()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        // わざと五十音とは逆の順で足す。並びは題名ではなく登録順のはず
        ws.AddTask(new TaskItem { Id = "t1", Title = "先に登録", Due = D(2026, 9, 24) });
        ws.AddTask(new TaskItem { Id = "t2", Title = "あとから登録", Due = D(2026, 9, 24) });
        ws.AddTask(new TaskItem { Id = "t3", Title = "さらにあとから登録", Due = D(2026, 9, 24) });

        var vm = Create(test, D(2026, 9, 24));

        Assert.Equal(["先に登録", "あとから登録", "さらにあとから登録"], vm.Tasks.Select(t => t.Title));
    }

    // ------------------------------------------------------------------
    // 同じ日の並び
    //
    // 時刻のあるものは時刻順。時刻を持たないものはカレンダーの並び順。
    // 旧 inaCalendar と同じ
    // ------------------------------------------------------------------

    [Fact]
    public void 時刻の無い予定はカレンダーの並び順で出す()
    {
        using var test = TestWorkspace.Create();

        // 左パネルでの並びは、作った順（sort_order）
        var first = test.Workspace.CreateCalendar("社内行事");
        var second = test.Workspace.CreateCalendar("生産ライン");

        // わざと逆の順で入れる。題の五十音でもない並びになる
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e2", Title = "あ", Date = D(2026, 9, 24), CalendarId = second.Id,
        });
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "い", Date = D(2026, 9, 24), CalendarId = first.Id,
        });

        var main = new MainViewModel(test.Workspace, today: D(2026, 9, 24));

        Assert.Equal(["e1", "e2"], main.SelectedDay.Events.Select(e => e.Id));
    }

    [Fact]
    public void 時刻のある予定は時刻順で出す()
    {
        using var test = TestWorkspace.Create();

        var late = test.Workspace.CreateCalendar("あとのカレンダー");
        var early = test.Workspace.CreateCalendar("さきのカレンダー");

        // 並び順では late が先。時刻があるときは時刻を優先する
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "午後", Date = D(2026, 9, 24), CalendarId = late.Id,
            StartTime = new TimeOnly(15, 0), EndTime = new TimeOnly(16, 0),
        });
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e2", Title = "午前", Date = D(2026, 9, 24), CalendarId = early.Id,
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        });

        var main = new MainViewModel(test.Workspace, today: D(2026, 9, 24));

        Assert.Equal(["e2", "e1"], main.SelectedDay.Events.Select(e => e.Id));
    }

    [Fact]
    public void 終日は時刻のあるものより先に出す()
    {
        using var test = TestWorkspace.Create();
        var calendar = test.Workspace.CreateCalendar("仕事");

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "朝一", Date = D(2026, 9, 24), CalendarId = calendar.Id,
            StartTime = new TimeOnly(0, 30), EndTime = new TimeOnly(1, 0),
        });
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e2", Title = "棚卸し", Date = D(2026, 9, 24), CalendarId = calendar.Id,
        });

        var main = new MainViewModel(test.Workspace, today: D(2026, 9, 24));

        Assert.Equal(["e2", "e1"], main.SelectedDay.Events.Select(e => e.Id));
    }

    [Fact]
    public void 済んだタスクは期限に間に合ったかを添える()
    {
        using var test = TestWorkspace.Create();
        var due = new DateOnly(2026, 9, 24);

        // 9/24 期限を 9/28（月）に済ませた
        test.Workspace.AddTask(new TaskItem
        {
            Id = "t1", Title = "資料作成", Due = due, IsDone = true,
            CompletedAt = LocalNoon(2026, 9, 28),
        });

        // 済ませた日（9/28）には、期限と遅れを添えて出る
        var vm = new SelectedDayViewModel(test.Workspace, D(2026, 9, 28), due);
        var row = vm.Tasks.Single(t => t.Id == "t1");

        Assert.Equal("期限 9/24・2実働日遅れ", row.DoneText);
        Assert.True(row.IsLate);
        Assert.False(row.IsFaint);
    }

    [Fact]
    public void 済んでいないタスクには結果を出さない()
    {
        using var test = TestWorkspace.Create();
        var due = new DateOnly(2026, 9, 24);

        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "資料作成", Due = due });

        var vm = new SelectedDayViewModel(test.Workspace, due, due);

        Assert.Null(vm.Tasks.Single(t => t.Id == "t1").DoneText);
    }
}
