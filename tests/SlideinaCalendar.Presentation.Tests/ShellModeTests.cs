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

        // 外したら留める前へそのまま戻る。ウィンドウから留めたのにスライドで
        // 返すと、画面から消えてしまって戻し方が分からなくなる
        vm.TogglePinCommand.Execute(null);
        Assert.Equal(ShellMode.Window, vm.Mode);

        Assert.Equal([ShellMode.Dock, ShellMode.Window], told);
    }

    [Fact]
    public void 出す位置は出しかたを変えずに選べる()
    {
        var vm = Create();

        // 位置を選んだだけで、ウィンドウのまま。出しかたの切り替えは
        // ボタンの左クリック（ToggleSlide）の役目
        vm.EdgeRightCommand.Execute(null);
        Assert.Equal(ShellMode.Window, vm.Mode);
        Assert.True(vm.IsAtRight);

        vm.EdgeLeftCommand.Execute(null);
        Assert.True(vm.IsAtLeft);

        // 留めているあいだに辺だけ変えても、留めたままにする
        vm.TogglePinCommand.Execute(null);
        vm.EdgeRightCommand.Execute(null);
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

    /// <summary>
    /// 出しかたボタン（項目1）のツールチップ。状態名ではなく、押したら何が
    /// 起きるかを短く書く。パネルの選び方は別のボタンに分けたので、ここには含めない
    /// </summary>
    [Fact]
    public void 出しかたボタンの文言は動作を短く言う()
    {
        var vm = Create();

        Assert.Equal("スライドにする（Ctrl＋Alt＋S）", vm.SlideActionLabel);

        vm.ToggleSlideCommand.Execute(null);
        Assert.Equal("ウィンドウに戻す", vm.SlideActionLabel);

        vm.TogglePinCommand.Execute(null);
        Assert.Equal("ウィンドウに戻す", vm.SlideActionLabel);
    }

    [Fact]
    public void いまの出しかたに名前が付く()
    {
        var vm = Create();

        Assert.Equal("ウィンドウ", vm.ModeLabel);

        vm.EdgeLeftCommand.Execute(null);
        vm.ToggleSlideCommand.Execute(null);
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

    /// <summary>
    /// <c>MainWindow.TrackPlacement</c>（App側、WPF なので Linux では検査できない）が
    /// 「端へ寄せている間は控えない」を成り立たせる前提。<c>Mode</c> のセッターは
    /// <c>_mode</c> を先に書き換えてから <c>ModeChanged</c> を出すので、
    /// <c>ModeChanged</c> の購読側（<c>ShellController.Apply</c>）が動く時点では
    /// <c>IsAtEdge</c> はもう行き先の値になっている。
    /// </summary>
    [Fact]
    public void ModeChangedが出る時点でIsAtEdgeは行き先のモードを指している()
    {
        var vm = Create();

        // ウィンドウ → オーバーレイ（端へ寄せる向き）。ModeChanged の中で見ても
        // もう true でなければ、寄せている最中の置き場所を「ウィンドウのとき」と
        // 誤って控えてしまう
        bool? atEdgeWhenToldToOverlay = null;
        vm.ModeChanged += (_, mode) =>
        {
            if (mode == ShellMode.Overlay) atEdgeWhenToldToOverlay = vm.IsAtEdge;
        };
        vm.ToggleSlideCommand.Execute(null);

        Assert.True(atEdgeWhenToldToOverlay);

        // オーバーレイ → ウィンドウ（戻す向き）。ModeChanged の中ではもう false で
        // なければ、ウィンドウへ戻ったはずの置き場所が控えられなくなる
        bool? atEdgeWhenToldToWindow = null;
        vm.ModeChanged += (_, mode) =>
        {
            if (mode == ShellMode.Window) atEdgeWhenToldToWindow = vm.IsAtEdge;
        };
        vm.ToggleSlideCommand.Execute(null);

        Assert.False(atEdgeWhenToldToWindow);
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
    public void スライドとピン留めは同じ幅を使う()
    {
        var vm = Create(ShellMode.Overlay);

        vm.DockWidth = 400;
        Assert.Equal(400, vm.DockWidth);

        // 留めても幅は変わらない。スライドとピン留めは表示内容・幅を揃え、
        // 違うのは出しかた（消えるか・居座るか）だけ
        vm.TogglePinCommand.Execute(null);
        Assert.Equal(400, vm.DockWidth);

        // ピン留め中に幅を変えても、外せば（スライドに戻っても）そのまま
        vm.DockWidth = 640;
        Assert.Equal(640, vm.DockWidth);

        vm.TogglePinCommand.Execute(null);
        Assert.Equal(640, vm.DockWidth);

        Assert.Equal(640, vm.Placement().Width);
    }

    [Fact]
    public void 狭いときだけ一列の形にする()
    {
        var vm = Create(ShellMode.Dock);

        // 幅が分からないうちは3ペインのまま
        Assert.True(vm.UsesWindowLayout);

        // 既定の帯幅では、まだ3ペインの形。狭いぶんはパネルを左 → 右 の順に
        // 畳んで凌ぐので、別の形へ化けるのは本当に置き場が無いときだけ
        vm.LayoutWidth = DockPlacement.DefaultWidth;
        Assert.True(vm.UsesWindowLayout);

        vm.LayoutWidth = DockPlacement.SidebarThreshold - 1;
        Assert.True(vm.UsesSidebarLayout);

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

    /// <summary>
    /// 旧バージョン（スライドとピン留めの幅を別々に控えていた版）からの移行。
    /// <para>
    /// 「ピン留めの幅を引き継ぐ」方針。利用者が最後に画面を分けて使っていた形に
    /// 近いため。既定値に戻す（＝古い値を捨てる）のは筋が悪い。
    /// </para>
    /// </summary>
    [Fact]
    public void 旧キーの固定幅が残っていれば新しい1つの幅として引き継ぐ()
    {
        var store = Store(out var connection);
        using var _ = connection;

        var settings = new SettingsRepository(connection);
        settings.Set("shell.width", "400");
        settings.Set("shell.docked_width", "640");

        var read = store.Load();

        // 捨てて既定値に戻すのではなく、固定（ピン留め）で使っていた幅を採る
        Assert.Equal(640, read.Width);

        // 引き継いだら、古いキーはもう残らない
        Assert.Null(settings.Get("shell.docked_width"));

        // 新しいキーにも書き戻され、次回からは移行を挟まず読める
        Assert.Equal("640", settings.Get("shell.width"));

        // 読み直しても同じ値が返る（一度きりの移行であることの確認）
        Assert.Equal(640, store.Load().Width);
    }

    [Fact]
    public void 旧キーが無ければ通常どおり読む()
    {
        var store = Store(out var connection);
        using var _ = connection;

        // 前の版から上げたときで、旧キー自体が無いケース（新規インストールなど）
        new SettingsRepository(connection).Set("shell.width", "400");

        var read = store.Load();

        Assert.Equal(400, read.Width);
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

    // ------------------------------------------------------------------
    // Esc での引っ込め（項目9）
    //
    // 実際に画面を動かすのは ShellController（App 側、Win32 が絡むので Linux では
    // 検査できない）。ここで確かめるのは、ShellViewModel から先へ頼みが伝わることと、
    // ウィンドウ居かたでは何もしないこと
    // ------------------------------------------------------------------

    [Fact]
    public void スライド中に頼むと伝わる()
    {
        var vm = Create(ShellMode.Overlay);

        var raised = 0;
        vm.RetractRequested += (_, _) => raised++;

        vm.RequestRetract();

        Assert.Equal(1, raised);
    }

    /// <summary>
    /// 固定（ピン留め）中も頼み自体は伝わる。実際に引っ込めない判断（ピン留めは
    /// 常駐が目的）は、これを受け取る ShellController 側（Win32 が絡むので
    /// Windows でしか検査できない）が持つ。
    /// </summary>
    [Fact]
    public void 固定中も頼み自体は伝わる()
    {
        var vm = Create(ShellMode.Dock);

        var raised = 0;
        vm.RetractRequested += (_, _) => raised++;

        vm.RequestRetract();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void ウィンドウ居かたでは頼んでも何も起きない()
    {
        var vm = Create(ShellMode.Window);

        var raised = 0;
        vm.RetractRequested += (_, _) => raised++;

        vm.RequestRetract();

        Assert.Equal(0, raised);
    }
}
