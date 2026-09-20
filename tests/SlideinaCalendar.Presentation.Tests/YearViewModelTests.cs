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
        new(test.Workspace, today ?? D(2026, 9, 24),
            sources: new SourceListsViewModel(test.Workspace));

    private static YearDayViewModel Day(YearViewModel vm, DateOnly date) =>
        vm.Months.Single(m => m.Year == date.Year && m.Month == date.Month)
            .Days.Single(d => d.DayNumber == date.Day);

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
        Assert.Null(vm.WorkingDayElapsed);
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

    // ------------------------------------------------------------------
    // 押した日を選べる
    // ------------------------------------------------------------------

    [Fact]
    public void 押した日に印が移る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 今日が選ばれた状態で始まる
        Assert.True(Day(vm, D(2026, 9, 24)).IsSelected);

        vm.SelectedDate = D(2026, 11, 5);

        Assert.True(Day(vm, D(2026, 11, 5)).IsSelected);
        Assert.False(Day(vm, D(2026, 9, 24)).IsSelected);

        // 印はひとつだけ
        Assert.Single(vm.Months.SelectMany(m => m.Days), d => d.IsSelected);
    }

    [Fact]
    public void 選び直しても同じマスを使い回す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        var before = Day(vm, D(2026, 11, 5));
        vm.SelectedDate = D(2026, 11, 5);

        // 12か月ぶん作り直すと、押すたびに目に見えて詰まる
        Assert.Same(before, Day(vm, D(2026, 11, 5)));
    }

    // ------------------------------------------------------------------
    // 予定の印
    // ------------------------------------------------------------------

    [Fact]
    public void 予定のある日に印が付く()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new Data.Models.CalendarEvent
        {
            Id = "e1", Title = "棚卸", Date = D(2026, 9, 24), CalendarId = "仕事",
        });

        var vm = Create(test);

        Assert.True(Day(vm, D(2026, 9, 24)).HasEvents);
        Assert.False(Day(vm, D(2026, 9, 25)).HasEvents);
    }

    [Fact]
    public void 印は多くても四本まで()
    {
        using var test = TestWorkspace.Create();

        for (var i = 0; i < 8; i++)
        {
            test.Workspace.AddEvent(new Data.Models.CalendarEvent
            {
                Id = $"e{i}", Title = $"会議{i}", Date = D(2026, 9, 24),
            });
        }

        // 増やすと日付が埋まって読めない
        Assert.Equal(4, Day(Create(test), D(2026, 9, 24)).Marks.Count);
    }

    [Fact]
    public void 日付の行のマイルストーンは印にしない()
    {
        using var test = TestWorkspace.Create(withMilestones: true);

        var vm = Create(test, D(2026, 9, 14));

        // マイルストーンは日付の行に出すもの。ここにも入れると二度数えられる
        Assert.False(Day(vm, D(2026, 9, 14)).HasEvents);
    }

    // ------------------------------------------------------------------
    // 縮尺と並び
    // ------------------------------------------------------------------

    [Fact]
    public void マスの幅は読める範囲に収める()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.DayWidth = 2;
        Assert.Equal(YearViewModel.MinDayWidth, vm.DayWidth);

        vm.DayWidth = 500;
        Assert.Equal(YearViewModel.MaxDayWidth, vm.DayWidth);

        vm.DayWidth = double.NaN;
        Assert.Equal(YearViewModel.DefaultDayWidth, vm.DayWidth);
    }

    [Fact]
    public void カレンダー表示は四半期ごとに縦へ並ぶ()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 入れ物は左から右へ詰めるので、並べる順のほうを入れ替えてある。
        // 4列なら1行目が 4月・7月・10月・1月
        Assert.Equal(4, vm.GridColumns);
        Assert.Equal(3, vm.GridRows);
        Assert.Equal([4, 7, 10, 1], vm.GridMonths.Take(4).Select(m => m.Month));
        Assert.Equal([5, 8, 11, 2], vm.GridMonths.Skip(4).Take(4).Select(m => m.Month));
        Assert.Equal([6, 9, 12, 3], vm.GridMonths.Skip(8).Select(m => m.Month));
    }

    [Fact]
    public void 狭いときは列を減らしても全部並ぶ()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.GridColumns = 2;

        Assert.Equal(6, vm.GridRows);
        Assert.Equal(12, vm.GridMonths.Count);
        Assert.Equal([4, 10], vm.GridMonths.Take(2).Select(m => m.Month));
    }

    [Fact]
    public void 目盛りは三十一マスぶん並ぶ()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 下の行と縦に揃える。数字を置くのは節目だけ
        Assert.Equal(31, vm.RulerMarks.Count);
        Assert.Equal("1", vm.RulerMarks[0].Label);
        Assert.Equal("", vm.RulerMarks[1].Label);
        Assert.Equal("5", vm.RulerMarks[4].Label);
        Assert.Equal("30", vm.RulerMarks[29].Label);
    }

    [Fact]
    public void 見出しは年度の範囲を出す()
    {
        using var test = TestWorkspace.Create();

        Assert.Equal("2026年度（2026年4月〜2027年3月）", Create(test).HeaderText);
    }
}
