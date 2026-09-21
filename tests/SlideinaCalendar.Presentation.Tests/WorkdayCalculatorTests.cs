using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

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
}
