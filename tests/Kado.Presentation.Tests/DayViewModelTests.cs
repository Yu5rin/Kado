using Kado.Data.Models;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

public class DayViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static TimeOnly T(int h, int m = 0) => new(h, m);

    private static DayViewModel Create(TestWorkspace test, DateOnly? date = null) =>
        new(test.Workspace, date ?? D(2026, 9, 24), today: D(2026, 9, 24));

    [Fact]
    public void 見出しは日付と曜日()
    {
        using var test = TestWorkspace.Create();

        Assert.Equal("9月24日（木）", Create(test).Title);
    }

    [Fact]
    public void 見出しに実働日の通し番号と月末までの残りを出す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.Equal("実働 15日目", vm.WorkingDayLabel);
        Assert.Equal("月末まで 4実働日", vm.RemainingInMonthText);
    }

    [Fact]
    public void 残りは表示している日を起点に数える()
    {
        using var test = TestWorkspace.Create();

        // 右ペインは今日を起点にするが、日ビューは見ている日の先行きを知りたい。
        // 起点の翌日から数えるので、9月の実働日 19 日のうち 9/1 を除いた 18 日
        Assert.Equal("月末まで 18実働日", Create(test, D(2026, 9, 1)).RemainingInMonthText);
    }

    [Fact]
    public void 実働日データの無い月では残りを出さない()
    {
        using var test = TestWorkspace.Create();

        Assert.Null(Create(test, D(2026, 10, 5)).RemainingInMonthText);
    }

    [Fact]
    public void 置き場所の規則は週ビューと同じで高さだけ違う()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "計画レビュー", Date = D(2026, 9, 24),
            StartTime = T(9), EndTime = T(10, 30),
        });

        var day = Create(test);
        var week = new WeekViewModel(test.Workspace, D(2026, 9, 24), D(2026, 9, 24));

        // 列が1本しかない日ビューは、モックどおり1時間を高く取る
        Assert.Equal(44, week.HourHeight);
        Assert.Equal(56, day.HourHeight);

        var fromDay = Assert.Single(day.Day.Blocks);
        var fromWeek = Assert.Single(week.Days.Single(d => d.Date == D(2026, 9, 24)).Blocks);

        // 規則は同じなので、開始位置は高さの比のまま
        Assert.Equal(56, fromDay.Top);
        Assert.Equal(44, fromWeek.Top);
        Assert.Equal(56 * 1.5 - 3, fromDay.Height);
        Assert.Equal(12 * 56, day.TimelineHeight);
    }

    [Fact]
    public void 日を送れる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.GoToNextDay();
        Assert.Equal(D(2026, 9, 25), vm.Date);
        Assert.Equal("9月25日（金）", vm.Title);

        vm.GoToPreviousDay();
        vm.GoToPreviousDay();
        Assert.Equal(D(2026, 9, 23), vm.Date);

        vm.GoToToday();
        Assert.Equal(D(2026, 9, 24), vm.Date);
    }

    [Fact]
    public void 現在時刻の線は今日を見ているときだけ出す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.UpdateNowLine(T(10));
        Assert.True(vm.ShowNowLine);
        Assert.Equal(112, vm.NowOffset);   // 8 時から 2 時間ぶん（56px × 2）

        vm.GoToNextDay();
        vm.UpdateNowLine(T(10));
        Assert.False(vm.ShowNowLine);
    }

    [Fact]
    public void 日を送ると中身も入れ替わる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "会議", Date = D(2026, 9, 25), StartTime = T(9), EndTime = T(10),
        });

        var vm = Create(test);
        Assert.Empty(vm.Day.Blocks);

        vm.GoToNextDay();
        Assert.Equal("会議", Assert.Single(vm.Day.Blocks).Title);
    }
}
