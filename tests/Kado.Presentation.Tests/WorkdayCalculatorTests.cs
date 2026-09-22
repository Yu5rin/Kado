using Kado.Core.WorkingDays;
using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 実働日計算パネル（要件書 4.5）。
/// <para>
/// テスト用のデータは 2026年9月ぶんだけ。稼働日は土日と 9/21〜23 を除く19日。
/// 9/1 は火曜なので、1・2・3・4／7〜11／14〜18／24・25／28〜30 が稼働日。
/// </para>
/// </summary>
public class WorkdayCalculatorTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static WorkdayCalculatorViewModel Create(TestWorkspace test) =>
        new(test.Workspace.WorkingDayMath, D(2026, 9, 24));

    [Fact]
    public void 期間の実働日数は両端を含めて数える()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 9/14（月）〜9/18（金）。人が「この期間で何日働くか」と聞くときは両端を含む
        vm.SetRange(D(2026, 9, 14), D(2026, 9, 18));

        Assert.Equal(5, vm.RangeCount);
        Assert.Equal(5, vm.RangeCalendarDays);
        Assert.Equal("5 実働日", vm.RangeResultText);
    }

    [Fact]
    public void 休みを挟むと暦日と実働日がずれる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 9/18（金）〜9/24（木）。19・20 は土日、21〜23 は休み
        vm.SetRange(D(2026, 9, 18), D(2026, 9, 24));

        Assert.Equal(2, vm.RangeCount);
        Assert.Equal(7, vm.RangeCalendarDays);
        Assert.Equal("暦日では 7 日（うち休業 5 日）", vm.RangeDetailText);
    }

    [Fact]
    public void 同じ日なら稼働日は一日休業日は零日()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SetRange(D(2026, 9, 24), D(2026, 9, 24));
        Assert.Equal(1, vm.RangeCount);

        vm.SetRange(D(2026, 9, 22), D(2026, 9, 22));
        Assert.Equal(0, vm.RangeCount);
    }

    [Fact]
    public void 逆向きに掴んでも同じ期間として受ける()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SetRange(D(2026, 9, 18), D(2026, 9, 14));

        Assert.Equal(D(2026, 9, 14), vm.RangeFrom);
        Assert.Equal(D(2026, 9, 18), vm.RangeTo);
        Assert.Equal(5, vm.RangeCount);
    }

    [Fact]
    public void 終わりを始まりより前に入れたら寄せる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.RangeFrom = D(2026, 9, 18);
        vm.RangeTo = D(2026, 9, 14);

        // 負の日数を出しても読めない
        Assert.Equal(D(2026, 9, 14), vm.RangeFrom);
        Assert.Equal(D(2026, 9, 14), vm.RangeTo);
    }

    [Fact]
    public void データの外は数えられないと断る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SetRange(D(2026, 8, 1), D(2026, 9, 18));

        Assert.Null(vm.RangeCount);
        Assert.False(vm.HasRangeResult);
        Assert.Equal("実働日データの範囲外です", vm.RangeResultText);
    }

    [Fact]
    public void 基準日から進んだ日を出す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 9/18 の次の稼働日は 9/24（19・20 は土日、21〜23 は休み）
        vm.BaseDate = D(2026, 9, 18);
        vm.Offset = 1;

        Assert.Equal(D(2026, 9, 24), vm.Arrival);
        Assert.Equal("2026年9月24日（木）", vm.ArrivalResultText);
        Assert.Equal("基準日から暦日で 6 日後", vm.ArrivalDetailText);
    }

    [Fact]
    public void 負の数を入れると遡る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.BaseDate = D(2026, 9, 24);
        vm.Offset = -1;

        Assert.Equal(D(2026, 9, 18), vm.Arrival);
        Assert.Equal("基準日から暦日で 6 日前", vm.ArrivalDetailText);
    }

    [Fact]
    public void 零なら基準日そのもの()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.BaseDate = D(2026, 9, 24);
        vm.Offset = 0;

        Assert.Equal(D(2026, 9, 24), vm.Arrival);
        Assert.Equal("基準日そのもの", vm.ArrivalDetailText);
    }

    [Fact]
    public void データが尽きたら出せないと断る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.BaseDate = D(2026, 9, 24);
        vm.Offset = 100;

        Assert.Null(vm.Arrival);
        Assert.False(vm.HasArrival);
        Assert.Equal("実働日データが足りません", vm.ArrivalResultText);
        Assert.Null(vm.ArrivalDetailText);
    }

    [Fact]
    public void 取り込み済みの範囲を出す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.True(vm.HasData);
        Assert.Equal("2026/9/1 〜 2026/9/30", vm.CoverageText);
    }

    [Fact]
    public void 実働日データが無ければその旨が分かる()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var vm = Create(test);

        Assert.False(vm.HasData);
        Assert.Null(vm.CoverageText);
        Assert.Null(vm.RangeCount);
    }

    [Fact]
    public void 暦日で数える設定にしていても実働で数える()
    {
        using var test = TestWorkspace.Create();

        // 暦日で数えるなら、この道具そのものが要らない（要件書 4.5）
        test.Workspace.CountInCalendarDays = true;

        var vm = new WorkdayCalculatorViewModel(test.Workspace.WorkingDayMath, D(2026, 9, 24));
        vm.SetRange(D(2026, 9, 18), D(2026, 9, 24));

        Assert.Equal(2, vm.RangeCount);
    }

    /// <summary>
    /// 項目7: モードレス化に伴い、窓が閉じられたことを ViewModel 側へ伝えられるようにした。
    /// <para><c>DialogEditorPresenter</c> が窓の <c>Closed</c> から呼ぶ。</para>
    /// </summary>
    [Fact]
    public void NotifyClosedでClosedイベントが上がる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        var raised = false;
        vm.Closed += (_, _) => raised = true;

        vm.NotifyClosed();

        Assert.True(raised);
    }

    // ------------------------------------------------------------------
    // 工程逆算（項目1）
    // ------------------------------------------------------------------

    [Fact]
    public void 既定のセットは4節目持つ()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        var plan = Assert.Single(vm.Plans);
        Assert.Same(plan, vm.SelectedPlan);
        Assert.Equal(4, plan.Steps.Count);
    }

    [Fact]
    public void 負のオフセットは基準日より前へ遡る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 9/25（金・稼働日）を納期として、-1 は直前の稼働日 9/24（木）
        vm.PlanDueDate = D(2026, 9, 25);
        vm.SelectedPlan!.Steps.Clear();
        vm.SelectedPlan.Steps.Add(new WorkdayStepEditRow("前工程", -1));

        var row = Assert.Single(vm.PlanRows);
        Assert.Equal(D(2026, 9, 24), row.Date);
        Assert.Equal("-1", row.OffsetText);
    }

    [Fact]
    public void 実働日データの範囲外の行だけ静かに断る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.PlanDueDate = D(2026, 9, 25);
        vm.SelectedPlan!.Steps.Clear();
        vm.SelectedPlan.Steps.Add(new WorkdayStepEditRow("近い節目", -1));
        vm.SelectedPlan.Steps.Add(new WorkdayStepEditRow("遠すぎる節目", -100));

        Assert.Equal(2, vm.PlanRows.Count);

        var near = vm.PlanRows[0];
        var far = vm.PlanRows[1];

        Assert.True(near.HasDate);
        Assert.False(far.HasDate);
        Assert.Equal("実働日データが足りません", far.DateText);

        // ほかの行はデータが足りていれば出せる。1行がだめでも全体を諦めない
        Assert.NotEqual("実働日データが足りません", near.DateText);
    }

    [Fact]
    public void 基準日が非稼働日でも逆算できる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 9/22（火）は休業日として登録されている（TestWorkspace のコメント参照）
        vm.PlanDueDate = D(2026, 9, 22);
        vm.SelectedPlan!.Steps.Clear();
        vm.SelectedPlan.Steps.Add(new WorkdayStepEditRow("前工程", -1));

        // 基準日自身は稼働日でなくてよい。直前の稼働日から1つ遡って 9/18（金）
        var row = Assert.Single(vm.PlanRows);
        Assert.Equal(D(2026, 9, 18), row.Date);
    }

    [Fact]
    public void うるう年の2月29日をまたいでも逆算できる()
    {
        // 2028年は閏年。2/1〜3/5 の平日をすべて稼働日として登録する
        var days = Enumerable.Range(0, 34)
            .Select(offset => D(2028, 2, 1).AddDays(offset))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .ToArray();

        var math = new WorkingDayMath(WorkingDayCalendar.Create(days));
        var vm = new WorkdayCalculatorViewModel(math, D(2028, 3, 1));

        // 3/1（水・稼働日）を納期に、-1 は 2/29（火）、-2 は 2/28（月）
        vm.PlanDueDate = D(2028, 3, 1);
        vm.SelectedPlan!.Steps.Clear();
        vm.SelectedPlan.Steps.Add(new WorkdayStepEditRow("前日", -1));
        vm.SelectedPlan.Steps.Add(new WorkdayStepEditRow("前々日", -2));

        Assert.Equal(D(2028, 2, 29), vm.PlanRows[0].Date);
        Assert.Equal(D(2028, 2, 28), vm.PlanRows[1].Date);
    }

    [Fact]
    public void 節目の追加削除並べ替えができる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SelectedPlan!.Steps.Clear();
        vm.AddStepCommand.Execute(null);
        vm.AddStepCommand.Execute(null);
        Assert.Equal(2, vm.SelectedPlan.Steps.Count);

        var first = vm.SelectedPlan.Steps[0];
        var second = vm.SelectedPlan.Steps[1];
        first.Name = "A";
        second.Name = "B";

        vm.MoveStepDownCommand.Execute(first);
        Assert.Equal(["B", "A"], vm.SelectedPlan.Steps.Select(s => s.Name));

        vm.RemoveStepCommand.Execute(first);
        Assert.Equal(["B"], vm.SelectedPlan.Steps.Select(s => s.Name));
    }

    [Fact]
    public void セットの追加と削除ができる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        var initialCount = vm.Plans.Count;

        vm.AddPlanCommand.Execute(null);
        Assert.Equal(initialCount + 1, vm.Plans.Count);
        Assert.Same(vm.Plans[^1], vm.SelectedPlan);

        vm.RemovePlanCommand.Execute(null);
        Assert.Equal(initialCount, vm.Plans.Count);
    }

    [Fact]
    public void セットの書き換えは設定へ保存され次に開いたときも残る()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);

        var vm = new WorkdayCalculatorViewModel(test.Workspace.WorkingDayMath, D(2026, 9, 24), settings);
        vm.SelectedPlan!.Name = "量産品";
        vm.SelectedPlan.Steps[0].Offset = -30;

        var reopened = new WorkdayCalculatorViewModel(test.Workspace.WorkingDayMath, D(2026, 9, 24), settings);

        Assert.Equal("量産品", reopened.SelectedPlan!.Name);
        Assert.Equal(-30, reopened.SelectedPlan.Steps[0].Offset);
    }

    [Fact]
    public void 各行から予定とタスクを作る窓口を呼べる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        DateOnly? forEvent = null;
        DateOnly? forTask = null;
        vm.CreateEventAt = d => forEvent = d;
        vm.CreateTaskAt = d => forTask = d;

        Assert.True(vm.HasCreateEventAction);
        Assert.True(vm.HasCreateTaskAction);

        vm.PlanDueDate = D(2026, 9, 25);
        var row = vm.PlanRows[0];

        vm.CreateEventFromPlanCommand.Execute(row);
        vm.CreateTaskFromPlanCommand.Execute(row);

        Assert.Equal(row.Date, forEvent);
        Assert.Equal(row.Date, forTask);
    }

    [Fact]
    public void 結果をタブ区切りでコピーへ渡す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.PlanDueDate = D(2026, 9, 25);
        vm.SelectedPlan!.Steps.Clear();
        vm.SelectedPlan.Steps.Add(new WorkdayStepEditRow("前工程", -1));

        string? copied = null;
        vm.CopyText = text => copied = text;

        vm.CopyPlanResultCommand.Execute(null);

        Assert.NotNull(copied);
        Assert.Contains("前工程\t-1\t", copied);
        Assert.Contains('\t', copied);
    }
}
