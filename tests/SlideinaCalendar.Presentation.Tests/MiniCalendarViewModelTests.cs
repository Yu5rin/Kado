using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

public class MiniCalendarViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static MiniCalendarViewModel Create(TestWorkspace test) =>
        new(test.Workspace, D(2026, 9, 1), today: D(2026, 9, 24));

    [Fact]
    public void 週の頭から週の終わりまで並ぶ()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // 2026/9/1 は火曜。日曜始まりなので 8/30 から、月末 9/30（水）を含む週の 10/3 まで
        Assert.Equal(D(2026, 8, 30), vm.Days[0].Date);
        Assert.Equal(D(2026, 10, 3), vm.Days[^1].Date);
        Assert.Equal(35, vm.Days.Count);

        Assert.False(vm.Days[0].IsCurrentMonth);
        Assert.True(vm.Days[2].IsCurrentMonth);
    }

    [Fact]
    public void 見出しは年月をそのまま出す()
    {
        using var test = TestWorkspace.Create();

        Assert.Equal("2026年9月", Create(test).Title);
        Assert.Equal(["日", "月", "火", "水", "木", "金", "土"], Create(test).WeekDayHeaders.Select(h => h.Name));
    }

    [Fact]
    public void 今日と選択日は別々に印が付く()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);
        vm.SelectedDate = D(2026, 9, 14);

        Assert.True(vm.Days.Single(d => d.Date == D(2026, 9, 24)).IsToday);
        Assert.True(vm.Days.Single(d => d.Date == D(2026, 9, 14)).IsSelected);
        Assert.False(vm.Days.Single(d => d.Date == D(2026, 9, 24)).IsSelected);
    }

    [Fact]
    public void 祝日は日曜と同じ色区分になる()
    {
        using var test = TestWorkspace.Create(
            holidays: new Dictionary<DateOnly, string> { [D(2026, 9, 21)] = "敬老の日" });

        var vm = Create(test);
        var holiday = vm.Days.Single(d => d.Date == D(2026, 9, 21));   // 月曜

        Assert.True(holiday.IsHoliday);
        Assert.True(holiday.IsSundayLike);
        Assert.False(holiday.IsSaturday);
    }

    [Fact]
    public void 月を送っても選択日は残る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);
        vm.SelectedDate = D(2026, 9, 14);

        vm.GoToNextMonth();

        Assert.Equal("2026年10月", vm.Title);
        Assert.Equal(D(2026, 9, 14), vm.SelectedDate);
        // 10月の枠には 9/14 が無いので、印の付いたマスも無い
        Assert.DoesNotContain(vm.Days, d => d.IsSelected);
    }
}
