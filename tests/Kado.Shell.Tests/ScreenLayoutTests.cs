using Kado.App.Shell;
using Kado.Presentation.Settings;
using static Kado.App.Shell.NativeMethods;

namespace Kado.Shell.Tests;

/// <summary>
/// 起動時の復元と、画面の構成が変わったときの置き直しの判断（<see cref="ShellGeometry"/>）。
/// <para>
/// 数値は実機の <c>shell.log</c> から取った。前日の最後は、ノート PC 単体（物理 2880x1824・
/// 拡大率 200%）で右端のスライドを出していた（<c>GetWindowRect=(2300,0,2880,1824)</c>）。
/// 当日のログオン起動は、1920x1080 の主画面と、その左に 1024x1280 の2画面だった。
/// 控えてあった位置が今の画面に無いとき、窓が全部の画面の外に置かれたり、存在しない
/// 画面の端を見張り続けたりしないことを確かめる。
/// </para>
/// </summary>
public class ScreenLayoutTests
{
    private static RECT Rect(int left, int top, int right, int bottom) =>
        new() { left = left, top = top, right = right, bottom = bottom };

    /// <summary>当日の主画面。作業領域はタスクバー（48px）のぶん低い。</summary>
    private static readonly ScreenInfo Primary =
        new(Rect(0, 0, 1920, 1080), Rect(0, 0, 1920, 1032), IsPrimary: true, Dpi: 96);

    /// <summary>当日の、主画面の左にある 1024x1280 の画面。</summary>
    private static readonly ScreenInfo LeftSide =
        new(Rect(-1024, 0, 0, 1280), Rect(-1024, 0, 0, 1280), IsPrimary: false, Dpi: 96);

    /// <summary>前日の構成：ノート PC 単体。物理 2880x1824、200%。</summary>
    private static readonly ScreenInfo Laptop =
        new(Rect(0, 0, 2880, 1824), Rect(0, 0, 2880, 1824), IsPrimary: true, Dpi: 192);

    /// <summary>当日の構成。列挙の順は Windows 任せなので、主画面が先とは限らない。</summary>
    private static readonly ScreenInfo[] Today = [LeftSide, Primary];

    /// <summary>前日の最後に右端へ出していたスライドの位置（物理ピクセル）。</summary>
    private static readonly RECT YesterdaySlide = Rect(2300, 0, 2880, 1824);

    // ------------------------------------------------------------------
    // 画面端（スライド・ピン留め）の置き場所
    // ------------------------------------------------------------------

    [Fact]
    public void 前日のノートPCの右端の位置は今の画面のどれにも乗っていない()
    {
        // 前提の確認。これが乗っていれば、ここから先の「置き直し」は要らない
        Assert.Equal(-1, ShellGeometry.BestOverlapIndex(YesterdaySlide, Today));
    }

    [Fact]
    public void 前日のノートPCの右端の位置のままログオンしたら主画面の右端へ置き直す()
    {
        var decision = ShellGeometry.DecideEdge(YesterdaySlide, DockEdge.Right, widthDip: 290, Today);

        Assert.True(decision.Relocated);
        Assert.Equal(1, decision.ScreenIndex);               // Today の並びでは主画面は 1 番目
        Assert.True(Today[decision.ScreenIndex].IsPrimary);

        // 作業領域の右端に、幅 290 で。左端は 1920 - 290 = 1630（ふだんの OffScreenLeft の resting と同じ）
        Assert.Equal(Rect(1630, 0, 1920, 1032), decision.Target);
    }

    [Fact]
    public void 置き直した画面が存在する画面である()
    {
        // 「存在しない画面の端を見張り続ける」ことが無い。選んだ画面は必ず今の一覧の中にある
        foreach (var edge in new[] { DockEdge.Left, DockEdge.Right })
        {
            var decision = ShellGeometry.DecideEdge(YesterdaySlide, edge, 290, Today);

            Assert.InRange(decision.ScreenIndex, 0, Today.Length - 1);
            Assert.True(decision.Target.left >= Today[decision.ScreenIndex].Monitor.left);
            Assert.True(decision.Target.right <= Today[decision.ScreenIndex].Monitor.right);
        }
    }

    [Fact]
    public void 左端なら主画面の左端へ置き直す()
    {
        var decision = ShellGeometry.DecideEdge(YesterdaySlide, DockEdge.Left, 290, Today);

        Assert.True(decision.Relocated);
        Assert.Equal(Rect(0, 0, 290, 1032), decision.Target);
    }

    [Fact]
    public void 窓が主画面に乗っているふだんのケースは今までどおりの位置()
    {
        // 保存位置が今の画面に乗っている普通のケース。動きを変えない
        var onPrimary = Rect(1630, 0, 1920, 1032);

        var decision = ShellGeometry.DecideEdge(onPrimary, DockEdge.Right, 290, Today);

        Assert.False(decision.Relocated);
        Assert.Equal(1, decision.ScreenIndex);
        Assert.Equal(Rect(1630, 0, 1920, 1032), decision.Target);
    }

    [Fact]
    public void 窓が左の画面に乗っているなら左の画面の端へ出す()
    {
        // どの画面に居るかは窓が決める（これまでどおり）。控えにある画面が今もあるなら動かさない
        var onLeftSide = Rect(-900, 100, -100, 900);

        var decision = ShellGeometry.DecideEdge(onLeftSide, DockEdge.Right, 290, Today);

        Assert.False(decision.Relocated);
        Assert.Equal(0, decision.ScreenIndex);
        Assert.Equal(Rect(-290, 0, 0, 1280), decision.Target);
    }

    [Fact]
    public void 画面をまたいでいれば広く乗っているほうを採る()
    {
        // 左の画面に 200px、主画面に 600px 掛かっている
        var straddling = Rect(-200, 100, 600, 700);

        var decision = ShellGeometry.DecideEdge(straddling, DockEdge.Right, 290, Today);

        Assert.Equal(1, decision.ScreenIndex);
        Assert.False(decision.Relocated);
    }

    [Fact]
    public void 前日の構成のままなら前日と同じ位置になる()
    {
        // 実機のログ：Left=1150（DIP）、GetWindowRect=(2300,0,2880,1824)。200% なので幅 290 は 580px
        var decision = ShellGeometry.DecideEdge(YesterdaySlide, DockEdge.Right, 290, [Laptop]);

        Assert.False(decision.Relocated);
        Assert.Equal(YesterdaySlide, decision.Target);
        Assert.Equal(1150, decision.Target.left / Laptop.Scale);
    }

    [Fact]
    public void 窓の矩形が取れなければ主画面に置く()
    {
        var decision = ShellGeometry.DecideEdge(null, DockEdge.Right, 290, Today);

        Assert.True(decision.Relocated);
        Assert.True(Today[decision.ScreenIndex].IsPrimary);
    }

    [Fact]
    public void 潰れた窓は画面に乗っているとは見なさない()
    {
        Assert.Equal(-1, ShellGeometry.BestOverlapIndex(Rect(100, 100, 100, 100), Today));
        Assert.Equal(-1, ShellGeometry.BestOverlapIndex(Rect(100, 100, 50, 300), Today));
    }

    [Fact]
    public void 画面の一覧が空でも落ちない()
    {
        var decision = ShellGeometry.DecideEdge(YesterdaySlide, DockEdge.Right, 290, []);

        Assert.Equal(-1, decision.ScreenIndex);
        Assert.False(decision.Relocated);

        var fit = ShellGeometry.RestoreWindow(Rect(10, 10, 800, 600), []);

        Assert.False(fit.Relocated);
        Assert.Equal(Rect(10, 10, 800, 600), fit.Placed);
    }

    [Fact]
    public void ログオン直後に左の画面だけが見えている途中の構成でも置き場所が決まる()
    {
        // 構成が落ち着く前は、主画面がまだ現れていないことがある。印の付いた画面が無ければ先頭を使う
        var transient = new[] { new ScreenInfo(LeftSide.Monitor, LeftSide.Work, IsPrimary: false, Dpi: 96) };

        var decision = ShellGeometry.DecideEdge(YesterdaySlide, DockEdge.Right, 290, transient);

        Assert.Equal(0, decision.ScreenIndex);
        Assert.Equal(Rect(-290, 0, 0, 1280), decision.Target);
    }

    [Fact]
    public void 倍率の違う画面へ置き直すときは置き直す先の倍率で幅を直す()
    {
        // 主画面が 150% のとき、幅 290（DIP）は 435px
        var scaled = new ScreenInfo(Rect(0, 0, 2880, 1620), Rect(0, 0, 2880, 1572), IsPrimary: true, Dpi: 144);

        var decision = ShellGeometry.DecideEdge(YesterdaySlide.Moved(10000), DockEdge.Right, 290, [scaled]);

        Assert.Equal(Rect(2880 - 435, 0, 2880, 1572), decision.Target);
    }

    // ------------------------------------------------------------------
    // 普通の窓
    // ------------------------------------------------------------------

    [Fact]
    public void 今の画面に乗っている普通の窓は動かさない()
    {
        var saved = Rect(370, 80, 1550, 840);

        var fit = ShellGeometry.RestoreWindow(saved, Today);

        Assert.False(fit.Relocated);
        Assert.Equal(saved, fit.Placed);
    }

    [Fact]
    public void 一部が画面の外にはみ出していても手が届くなら動かさない()
    {
        // 右へ 300px はみ出している。タイトルバーは見えているので、利用者が置いたとおりにする
        var saved = Rect(1040, 100, 2220, 860);

        var fit = ShellGeometry.RestoreWindow(saved, Today);

        Assert.False(fit.Relocated);
        Assert.Equal(saved, fit.Placed);
    }

    [Fact]
    public void 最大化した窓の見えない枠のぶんのはみ出しは動かさない()
    {
        var maximized = Rect(-8, -8, 1928, 1040);

        var fit = ShellGeometry.RestoreWindow(maximized, Today);

        Assert.False(fit.Relocated);
    }

    [Fact]
    public void 前の画面にあった窓はどの画面にも乗っていないので主画面へ収める()
    {
        // 前日のノート PC の中（物理 2880 幅）に居た窓。今の構成には、その場所に画面が無い
        var saved = Rect(2000, 100, 3180, 860);

        var fit = ShellGeometry.RestoreWindow(saved, Today);

        Assert.True(fit.Relocated);
        Assert.True(Today[fit.ScreenIndex].IsPrimary);
        AssertInside(fit.Placed, Primary.Work);

        // 大きさは保つ
        Assert.Equal(saved.Width, fit.Placed.Width);
        Assert.Equal(saved.Height, fit.Placed.Height);
    }

    [Fact]
    public void ほんの端だけ残っている窓も主画面へ収める()
    {
        // 左の画面の左端から 40px だけ見えている。掴んで引き戻すのは難しい
        var saved = Rect(-1024 - 1140, 100, -1024 + 40, 860);

        var fit = ShellGeometry.RestoreWindow(saved, Today);

        Assert.True(fit.Relocated);
        AssertInside(fit.Placed, Primary.Work);
    }

    [Fact]
    public void タイトルバーが画面の下の外にある窓は主画面へ収める()
    {
        // 横は十分に乗っているが、上端が画面の下端より外
        var saved = Rect(300, 1100, 1480, 1860);

        var fit = ShellGeometry.RestoreWindow(saved, Today);

        Assert.True(fit.Relocated);
        AssertInside(fit.Placed, Primary.Work);
    }

    [Fact]
    public void 主画面より大きな窓は主画面に収まるまで縮める()
    {
        var saved = Rect(5000, 0, 8000, 2400);

        var fit = ShellGeometry.RestoreWindow(saved, Today);

        Assert.True(fit.Relocated);
        AssertInside(fit.Placed, Primary.Work);
        Assert.Equal(Primary.Work.Width, fit.Placed.Width);
        Assert.Equal(Primary.Work.Height, fit.Placed.Height);
    }

    [Fact]
    public void どの画面にも掛かっていない窓は主画面の真ん中に置く()
    {
        var saved = Rect(4000, 3000, 5180, 3760);

        var fit = ShellGeometry.RestoreWindow(saved, Today);

        Assert.True(fit.Relocated);
        Assert.Equal((Primary.Work.Width - 1180) / 2, fit.Placed.left);
        Assert.Equal((Primary.Work.Height - 760) / 2, fit.Placed.top);
    }

    [Fact]
    public void 主画面にわずかに掛かっているだけなら今の位置から最小限だけ押し戻す()
    {
        // 右端から 60px だけ見えている。上は動かさず、左へ寄せて収める
        var saved = Rect(1860, 100, 3040, 860);

        var fit = ShellGeometry.RestoreWindow(saved, Today);

        Assert.True(fit.Relocated);
        Assert.Equal(100, fit.Placed.top);
        Assert.Equal(1920 - 1180, fit.Placed.left);
    }

    [Fact]
    public void 手が届くかの判定は画面ごとに見る()
    {
        // 左の画面と主画面の境目に跨る窓は、どちらか広いほうで足りていれば届く
        Assert.True(ShellGeometry.IsReachable(Rect(-600, 100, 580, 860), Today));

        // 外接矩形の中でも、どの画面でもない場所（主画面の下の隙間）は届かない
        Assert.False(ShellGeometry.IsReachable(Rect(300, 1200, 1480, 1960), Today));
    }

    // ------------------------------------------------------------------
    // 画面の構成の比べ方（画面の構成が変わったか）
    // ------------------------------------------------------------------

    [Fact]
    public void 並びが違うだけなら同じ構成()
    {
        Assert.True(ShellGeometry.SameLayout([LeftSide, Primary], [Primary, LeftSide]));
    }

    [Fact]
    public void 画面が増えたり減ったりしたら違う構成()
    {
        Assert.False(ShellGeometry.SameLayout([Primary], Today));
        Assert.False(ShellGeometry.SameLayout(Today, [Primary]));
    }

    [Fact]
    public void 倍率だけ変わっても違う構成()
    {
        var scaled = Primary with { Dpi = 144 };

        Assert.False(ShellGeometry.SameLayout([Primary], [scaled]));
    }

    [Fact]
    public void 作業領域だけ変わっても違う構成()
    {
        // タスクバーを動かした
        var moved = Primary with { Work = Rect(0, 0, 1872, 1080) };

        Assert.False(ShellGeometry.SameLayout([Primary], [moved]));
    }

    [Fact]
    public void 前日の構成から当日の構成への移り変わりは違う構成()
    {
        Assert.False(ShellGeometry.SameLayout([Laptop], Today));
    }

    // ------------------------------------------------------------------
    // shell.log の行
    // ------------------------------------------------------------------

    [Fact]
    public void 画面の一覧は_startup_行と同じ並びで主画面の印が付く()
    {
        Assert.Equal(
            "[[-1024,0,0,1280]@96x96 [0,0,1920,1080]主@96x96]",
            ShellGeometry.DescribeScreens(Today));
    }

    [Fact]
    public void 起動時の復元の行に保存値_今の画面_置き直したかが出る()
    {
        var decision = ShellGeometry.DecideEdge(YesterdaySlide, DockEdge.Right, 290, Today);

        var line = ShellGeometry.FormatEdgeRestoreLine(
            ShellMode.Overlay, DockEdge.Right, 290, YesterdaySlide, Today, decision);

        Assert.Equal(
            "startup-restore mode=Overlay edge=Right dockWidth=290.0 saved=(2300,0,2880,1824) " +
            "screens=[[-1024,0,0,1280]@96x96 [0,0,1920,1080]主@96x96] → screen=#1(0,0,1920,1080) " +
            "置き直し=はい 置き場所=(1630,0,1920,1032) " +
            "理由=保存されていた位置はどの画面にも乗っていないので、主画面の同じ端へ置いた",
            line);
    }

    [Fact]
    public void 置き直さなかった行は_いいえ_と出る()
    {
        var onPrimary = Rect(1630, 0, 1920, 1032);
        var decision = ShellGeometry.DecideEdge(onPrimary, DockEdge.Right, 290, Today);

        var line = ShellGeometry.FormatEdgeRestoreLine(
            ShellMode.Overlay, DockEdge.Right, 290, onPrimary, Today, decision);

        Assert.Contains("置き直し=いいえ", line);
        Assert.Contains("saved=(1630,0,1920,1032)", line);
    }

    [Fact]
    public void 窓の矩形が取れなかったことも行に出る()
    {
        var decision = ShellGeometry.DecideEdge(null, DockEdge.Right, 290, Today);

        var line = ShellGeometry.FormatEdgeRestoreLine(
            ShellMode.Overlay, DockEdge.Right, 290, null, Today, decision);

        Assert.Contains("saved=取得不可", line);
    }

    [Fact]
    public void 普通の窓の復元の行にも保存値と置き直しが出る()
    {
        var saved = Rect(2000, 100, 3180, 860);
        var fit = ShellGeometry.RestoreWindow(saved, Today);

        var line = ShellGeometry.FormatWindowRestoreLine(saved, Today, fit);

        Assert.StartsWith("startup-restore mode=Window saved=(2000,100,3180,860) ", line);
        Assert.Contains("置き直し=はい", line);
        Assert.Contains("理由=どの画面にも乗っていないので、主画面へ置き直した", line);
    }

    private static void AssertInside(RECT inner, RECT outer)
    {
        Assert.True(inner.left >= outer.left, $"左がはみ出している: {ShellGeometry.FormatRect(inner)}");
        Assert.True(inner.top >= outer.top, $"上がはみ出している: {ShellGeometry.FormatRect(inner)}");
        Assert.True(inner.right <= outer.right, $"右がはみ出している: {ShellGeometry.FormatRect(inner)}");
        Assert.True(inner.bottom <= outer.bottom, $"下がはみ出している: {ShellGeometry.FormatRect(inner)}");
    }
}

internal static class RectTestExtensions
{
    /// <summary>横へ <paramref name="dx"/> だけずらした矩形。</summary>
    internal static RECT Moved(this RECT rect, int dx) =>
        new() { left = rect.left + dx, top = rect.top, right = rect.right + dx, bottom = rect.bottom };
}
