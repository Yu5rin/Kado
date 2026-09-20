using SlideinaCalendar.Data;
using SlideinaCalendar.Data.Repositories;
using SlideinaCalendar.Presentation.Settings;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 画面での居かた（要件書 2章）。
/// <para>
/// ウィンドウ → オーバーレイ → ドック。オーバーレイは重なるだけで、ドックにして
/// 初めてワークエリアを削る。
/// </para>
/// </summary>
public class ShellModeTests
{
    private static ShellViewModel Create(ShellMode mode = ShellMode.Window) =>
        new(DockPlacement.Unknown with { Mode = mode });

    [Fact]
    public void ウィンドウからでもひと押しで留まりもう一度押すと戻る()
    {
        var vm = Create();

        var told = new List<ShellMode>();
        vm.ModeChanged += (_, mode) => told.Add(mode);

        // 端へ寄せてから押す作りだと、Windows のスナップと取り合いになって
        // 寄せたつもりでも留められないことがあった
        vm.TogglePinCommand.Execute(null);
        Assert.True(vm.IsPinned);

        // 外したらスライドに戻す。ウィンドウから留めた場合も、そのまま画面端に
        // 居続けるほうが「出しておきたくて留めた」流れに合う
        vm.TogglePinCommand.Execute(null);
        Assert.Equal(ShellMode.Overlay, vm.Mode);

        Assert.Equal([ShellMode.Dock, ShellMode.Overlay], told);
    }

    [Fact]
    public void スライドは辺を選んで切り替える()
    {
        var vm = Create();

        vm.SlideRightCommand.Execute(null);
        Assert.Equal(ShellMode.Overlay, vm.Mode);
        Assert.True(vm.IsAtRight);

        vm.SlideLeftCommand.Execute(null);
        Assert.True(vm.IsAtLeft);

        // 留めているあいだに辺だけ変えても、留めたままにする
        vm.TogglePinCommand.Execute(null);
        vm.SlideRightCommand.Execute(null);
        Assert.True(vm.IsPinned);
        Assert.True(vm.IsAtRight);
    }

    [Fact]
    public void ウィンドウとスライドを行き来できる()
    {
        var vm = Create();

        vm.ToggleSlideCommand.Execute(null);
        Assert.Equal(ShellMode.Overlay, vm.Mode);

        vm.ToggleSlideCommand.Execute(null);
        Assert.Equal(ShellMode.Window, vm.Mode);
    }

    [Fact]
    public void いまの出しかたに名前が付く()
    {
        var vm = Create();

        Assert.Equal("ウィンドウ", vm.ModeLabel);

        vm.SlideLeftCommand.Execute(null);
        Assert.Equal("スライド（左）", vm.ModeLabel);

        vm.TogglePinCommand.Execute(null);
        Assert.Equal("固定（左）", vm.ModeLabel);
    }

    [Fact]
    public void オーバーレイから留めたらオーバーレイに戻る()
    {
        var vm = Create(ShellMode.Overlay);

        vm.TogglePinCommand.Execute(null);
        Assert.True(vm.IsPinned);

        vm.TogglePinCommand.Execute(null);
        Assert.Equal(ShellMode.Overlay, vm.Mode);
    }

    [Fact]
    public void ワークエリアを削るのはドックのときだけ()
    {
        Assert.False(DockPlacement.Unknown.ReservesWorkArea);
        Assert.False((DockPlacement.Unknown with { Mode = ShellMode.Overlay }).ReservesWorkArea);
        Assert.True((DockPlacement.Unknown with { Mode = ShellMode.Dock }).ReservesWorkArea);
    }

    [Fact]
    public void 画面端に居るのはオーバーレイとドック()
    {
        Assert.False(Create().IsAtEdge);
        Assert.True(Create(ShellMode.Overlay).IsAtEdge);
        Assert.True(Create(ShellMode.Dock).IsAtEdge);
    }

    [Fact]
    public void 寄せる辺を切り替えられる()
    {
        var vm = Create(ShellMode.Dock);

        DockEdge? told = null;
        vm.EdgeChanged += (_, edge) => told = edge;

        // 既定は左。右利きの画面では左端のほうが邪魔になりにくい
        Assert.True(vm.IsAtLeft);

        vm.ToggleEdgeCommand.Execute(null);

        Assert.True(vm.IsAtRight);
        Assert.Equal(DockEdge.Right, told);
    }

    [Fact]
    public void 幅は使える範囲に収める()
    {
        var vm = Create(ShellMode.Dock);

        vm.DockWidth = 10;
        Assert.Equal(DockPlacement.MinWidth, vm.DockWidth);

        vm.DockWidth = 5000;
        Assert.Equal(DockPlacement.MaxWidth, vm.DockWidth);

        vm.DockWidth = double.NaN;
        Assert.Equal(DockPlacement.DefaultWidth, vm.DockWidth);
    }

    [Fact]
    public void 狭いときだけ一列の形にする()
    {
        var vm = Create(ShellMode.Dock);

        // 幅が分からないうちは3ペインのまま
        Assert.True(vm.UsesWindowLayout);

        vm.LayoutWidth = DockPlacement.DefaultWidth;
        Assert.True(vm.UsesSidebarLayout);

        // この幅で3ペインに分けると、各ペインが3行しか入らない（要件書 5.3）
        vm.LayoutWidth = 1180;
        Assert.True(vm.UsesWindowLayout);
        Assert.False(vm.UsesSidebarLayout);
    }

    [Fact]
    public void サイドバーの下半分はタブで切り替える()
    {
        var vm = Create(ShellMode.Dock);

        Assert.True(vm.ShowsEvents);

        vm.ShowTasksCommand.Execute(null);
        Assert.True(vm.ShowsTasks);
        Assert.False(vm.ShowsEvents);

        vm.ShowEventsCommand.Execute(null);
        Assert.True(vm.ShowsEvents);
    }

    // ------------------------------------------------------------------
    // 居場所の出し入れ
    // ------------------------------------------------------------------

    private static DockPlacementStore Store(out Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();
        return new DockPlacementStore(new SettingsRepository(connection));
    }

    [Fact]
    public void 居場所を控えて読み戻す()
    {
        var store = Store(out var connection);
        using var _ = connection;

        store.Save(new DockPlacement(ShellMode.Dock, DockEdge.Left, 400, @"\\.\DISPLAY2"));

        var read = store.Load();

        Assert.Equal(ShellMode.Dock, read.Mode);
        Assert.Equal(DockEdge.Left, read.Edge);
        Assert.Equal(400, read.Width);
        Assert.Equal(@"\\.\DISPLAY2", read.MonitorId);
    }

    [Fact]
    public void 何も控えていなければウィンドウとして始める()
    {
        var store = Store(out var connection);
        using var _ = connection;

        var read = store.Load();

        Assert.Equal(ShellMode.Window, read.Mode);
        Assert.Equal(DockEdge.Left, read.Edge);
        Assert.Equal(DockPlacement.DefaultWidth, read.Width);
        Assert.Null(read.MonitorId);
    }

    [Fact]
    public void 壊れた値は既定に戻す()
    {
        var connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();
        using var _ = connection;

        var settings = new SettingsRepository(connection);
        settings.Set("shell.mode", "そんなモードは無い");
        settings.Set("shell.width", "とても広い");

        var read = new DockPlacementStore(settings).Load();

        Assert.Equal(ShellMode.Window, read.Mode);
        Assert.Equal(DockPlacement.DefaultWidth, read.Width);
    }

    // ------------------------------------------------------------------
    // 安全装置（要件書 2.3）
    //
    // ABM_REMOVE を呼ばずに落ちると、ワークエリアが削られたまま残り、
    // ユーザーのデスクトップが壊れる
    // ------------------------------------------------------------------

    [Fact]
    public void 削ったまま終わったことが次の起動で分かる()
    {
        var store = Store(out var connection);
        using var _ = connection;

        Assert.False(store.WasWorkAreaLeftReserved());

        // 削る前に印を付ける
        store.SetWorkAreaReserved(true);
        Assert.True(store.WasWorkAreaLeftReserved());

        // きれいに戻したら消す
        store.SetWorkAreaReserved(false);
        Assert.False(store.WasWorkAreaLeftReserved());
    }

    [Fact]
    public void 印は別の接続からも読める()
    {
        var connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();
        using var _ = connection;

        // 落ちたあとの再起動を模す。同じデータベースを読み直す
        new DockPlacementStore(new SettingsRepository(connection)).SetWorkAreaReserved(true);

        Assert.True(new DockPlacementStore(new SettingsRepository(connection))
            .WasWorkAreaLeftReserved());
    }

    // ------------------------------------------------------------------
    // 幅の収め方
    // ------------------------------------------------------------------

    [Fact]
    public void 画面の半分を超えて削らない()
    {
        // 削りすぎると、元の作業をする場所が残らない
        var placement = new DockPlacement(ShellMode.Dock, DockEdge.Right, 700, null)
            .ClampTo(1000);

        Assert.Equal(500, placement.Width);
    }

    [Fact]
    public void 画面が小さくても下限は割らない()
    {
        // 中身が読めないほど細い帯を置いても仕方がない
        var placement = new DockPlacement(ShellMode.Dock, DockEdge.Right, 400, null)
            .ClampTo(400);

        Assert.Equal(DockPlacement.MinWidth, placement.Width);
    }

    [Fact]
    public void 画面の幅が分からなければ幅だけ整える()
    {
        var placement = new DockPlacement(ShellMode.Dock, DockEdge.Right, 5000, null)
            .ClampTo(0);

        Assert.Equal(DockPlacement.MaxWidth, placement.Width);
    }
}
