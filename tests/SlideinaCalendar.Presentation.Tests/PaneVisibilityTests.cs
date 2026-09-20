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
