using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 年ビュー。
/// <para>
/// 年度単位（4月〜翌3月）。会社の実働日カレンダーが年度で配られるので、暦年で
/// 区切ると配布物と突き合わせられない。
/// </para>
/// </summary>
public class YearViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static YearViewModel Create(TestWorkspace test, DateOnly? today = null) =>
        new(test.Workspace, today ?? D(2026, 9, 24));

    [Fact]
    public void 年度は四月から翌年三月まで()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.Equal(12, vm.Months.Count);
        Assert.Equal((2026, 4), (vm.Months[0].Year, vm.Months[0].Month));
        Assert.Equal((2027, 3), (vm.Months[^1].Year, vm.Months[^1].Month));
    }

    [Fact]
    public void 一月から三月は前の年の年度になる()
    {
        using var test = TestWorkspace.Create();

        // 2027年2月は 2026年度
        Assert.Equal(2026, YearViewModel.FiscalYearOf(D(2027, 2, 10)));
        Assert.Equal(2027, YearViewModel.FiscalYearOf(D(2027, 4, 1)));

        var vm = Create(test, D(2027, 2, 10));
        Assert.Equal(2026, vm.FiscalYear);
    }

    [Fact]
    public void 上期と下期に分けて並べる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 半期ビューは設けず、行のまとまりで示す（要件書 5.1）
        Assert.Equal([4, 5, 6, 7, 8, 9], vm.FirstHalf.Select(m => m.Month));
        Assert.Equal([10, 11, 12, 1, 2, 3], vm.SecondHalf.Select(m => m.Month));
    }

    [Fact]
    public void 月末より後ろには枠を作らない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 空の枠を置くと、月によって右端がぶれて縦の並びが読めない
        Assert.Equal(30, vm.Months.Single(m => m.Month == 4).Days.Count);
        Assert.Equal(31, vm.Months.Single(m => m.Month == 5).Days.Count);
        Assert.Equal(28, vm.Months.Single(m => m is { Month: 2, Year: 2027 }).Days.Count);
    }

    [Fact]
    public void 実働日データを持つ月だけ日数を出す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // テスト用のデータは2026年9月ぶんだけ
        Assert.Equal(19, vm.Months.Single(m => m.Month == 9).WorkingDayCount);
        Assert.Null(vm.Months.Single(m => m.Month == 4).WorkingDayCount);
        Assert.Equal("−", vm.Months.Single(m => m.Month == 4).WorkingDayCountText);
    }

    [Fact]
    public void 揃っていない年度の合計は出さない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 足りないまま足すと、本当より少ない数を正しい数として読んでしまう
        Assert.Null(vm.WorkingDayTotal);
        Assert.Equal("実働日データが揃っていません", vm.WorkingDayTotalText);
    }

    [Fact]
    public void 稼働日と休業日を見分ける()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        var september = vm.Months.Single(m => m.Month == 9);

        var workday = september.Days.Single(d => d.DayNumber == 24);
        Assert.True(workday.HasWorkingDayData);
        Assert.True(workday.IsWorkingDay);
        Assert.False(workday.IsOffDay);

        // 9/21〜23 は休み
        Assert.True(september.Days.Single(d => d.DayNumber == 22).IsOffDay);

        // データを持たない月は塗らない
        Assert.All(vm.Months.Single(m => m.Month == 4).Days, d => Assert.False(d.IsOffDay));
    }

    [Fact]
    public void 今日を含む月の行が分かる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.Single(vm.Months, m => m.HasToday);
        Assert.Equal(9, vm.Months.Single(m => m.HasToday).Month);
    }

    [Fact]
    public void 年度を送れる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.GoToNextYear();
        Assert.Equal(2027, vm.FiscalYear);
        Assert.Equal((2027, 4), (vm.Months[0].Year, vm.Months[0].Month));

        vm.GoToPreviousYear();
        vm.GoToPreviousYear();
        Assert.Equal(2025, vm.FiscalYear);

        vm.GoToToday();
        Assert.Equal(2026, vm.FiscalYear);
    }

    [Fact]
    public void 年度の外を選んだらその年度へ移る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SelectedDate = D(2028, 5, 1);

        Assert.Equal(2028, vm.FiscalYear);
    }

    [Fact]
    public void 出し方を切り替えると知らせる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        YearLayout? told = null;
        vm.LayoutChanged += (_, layout) => told = layout;

        Assert.True(vm.IsStrip);

        vm.Layout = YearLayout.Grid;

        // 設定に控えるのは持ち主の仕事。次の起動でも同じ形で出すため
        Assert.Equal(YearLayout.Grid, told);
        Assert.True(vm.IsGrid);
        Assert.False(vm.IsStrip);
    }

    [Fact]
    public void 見出しは年度の範囲を出す()
    {
        using var test = TestWorkspace.Create();

        Assert.Equal("2026年度（2026年4月〜2027年3月）", Create(test).HeaderText);
    }
}
