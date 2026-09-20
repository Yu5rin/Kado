using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 出すパネルの選び方。
/// <para>
/// 画面端に細く留めているときは、カレンダー本体を畳んで予定だけを見たいことがある。
/// </para>
/// </summary>
public class PaneVisibilityTests
{
    private static MainViewModel Create(TestWorkspace test) =>
        new(test.Workspace, new DateOnly(2026, 9, 24));

    [Fact]
    public void はじめは三つとも出ている()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.True(vm.IsSidePanelOpen);
        Assert.True(vm.IsMainViewOpen);
        Assert.True(vm.IsDetailPaneOpen);
    }

    [Fact]
    public void それぞれ畳める()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.ToggleMainViewCommand.Execute(null);
        Assert.False(vm.IsMainViewOpen);
        Assert.True(vm.IsSidePanelOpen);
        Assert.True(vm.IsDetailPaneOpen);

        vm.ToggleDetailPaneCommand.Execute(null);
        Assert.False(vm.IsDetailPaneOpen);
    }

    [Fact]
    public void 中央を畳むとカレンダーの操作を出さない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.True(vm.ShowsCalendarTools);

        vm.ToggleMainViewCommand.Execute(null);

        // 出したままだと、押しても何も起きないボタンが並ぶ
        Assert.False(vm.ShowsCalendarTools);
    }

    [Fact]
    public void 中央を畳んだら送りは日ごとになる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.ToggleMainViewCommand.Execute(null);

        // 出ているのは選んだ日の予定だけ。月を送っても手応えが無い
        vm.NextCommand.Execute(null);
        Assert.Equal(new DateOnly(2026, 9, 25), vm.SelectedDate);

        vm.PreviousCommand.Execute(null);
        vm.PreviousCommand.Execute(null);
        Assert.Equal(new DateOnly(2026, 9, 23), vm.SelectedDate);
    }

    [Fact]
    public void 中央を畳んだら見出しが日付になる()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.Equal("9月", vm.TitleMonth);

        vm.ToggleMainViewCommand.Execute(null);

        Assert.Equal("9月24日", vm.TitleMonth);
        Assert.Equal("2026", vm.TitleYear);

        vm.NextCommand.Execute(null);
        Assert.Equal("9月25日", vm.TitleMonth);
    }

    [Fact]
    public void 中央を戻したら送りは月ごとに戻る()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.ToggleMainViewCommand.Execute(null);
        vm.ToggleMainViewCommand.Execute(null);

        vm.NextCommand.Execute(null);

        Assert.Equal(10, vm.Month.Month.Month);
    }

    [Fact]
    public void 三つとも畳もうとしたら最後の一つは残す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.ToggleSidePanelCommand.Execute(null);
        vm.ToggleDetailPaneCommand.Execute(null);
        vm.ToggleMainViewCommand.Execute(null);

        // 全部消すと、窓だけがそこにあって何もできなくなる
        Assert.True(vm.IsSidePanelOpen || vm.IsMainViewOpen || vm.IsDetailPaneOpen);
    }

    [Fact]
    public void 右ペインだけのときは日送りを日付欄へ移す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        // ふだんはツールバーに年月と「◀ ▶」を出す
        Assert.True(vm.ShowsToolbarDate);
        Assert.False(vm.ShowsDayNav);

        vm.ToggleSidePanelCommand.Execute(null);
        vm.ToggleMainViewCommand.Execute(null);

        // 右ペインだけになったら、ツールバーの年月は日付欄と同じことを
        // 二度言うことになる。送りも日付のすぐ隣へ移す
        Assert.True(vm.ShowsDayNav);
        Assert.False(vm.ShowsToolbarDate);

        // 右ペインも畳んだら（左パネルだけ）、置き場はツールバーに戻る
        vm.ToggleSidePanelCommand.Execute(null);
        vm.ToggleDetailPaneCommand.Execute(null);

        Assert.False(vm.ShowsDayNav);
        Assert.True(vm.ShowsToolbarDate);
    }

    [Fact]
    public void 中央を最後に畳んだら右パネルを残す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.ToggleSidePanelCommand.Execute(null);
        vm.ToggleDetailPaneCommand.Execute(null);
        vm.ToggleMainViewCommand.Execute(null);

        Assert.True(vm.IsDetailPaneOpen);
        Assert.False(vm.IsMainViewOpen);
    }
}
