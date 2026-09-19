using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

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
        using var test = TestWorkspace.Create();

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
    public void 未完了が先_完了はその日のぶんだけ添える()
    {
        using var test = TestWorkspace.Create();
        var ws = test.Workspace;

        ws.AddTask(new TaskItem { Id = "t1", Title = "残り", Due = D(2026, 9, 25) });
        ws.AddTask(new TaskItem { Id = "t2", Title = "今日片付けた", Due = D(2026, 9, 24), IsDone = true });
        ws.AddTask(new TaskItem { Id = "t3", Title = "前に片付けた", Due = D(2026, 9, 10), IsDone = true });

        var vm = Create(test, D(2026, 9, 24));

        // 過去の完了が積み上がると読めない
        Assert.Equal(["残り", "今日片付けた"], vm.Tasks.Select(t => t.Title));
        Assert.Equal("1 / 2", vm.TaskCountText);
    }

    [Fact]
    public void 期限の無いタスクは右ペインに出さない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "いつかやる" });

        Assert.Empty(Create(test, D(2026, 9, 24)).Tasks);
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
}
