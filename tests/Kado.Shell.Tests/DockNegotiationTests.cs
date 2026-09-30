using Kado.App.Shell;
using Kado.Presentation.Settings;
using static Kado.App.Shell.NativeMethods;

namespace Kado.Shell.Tests;

/// <summary>
/// ピン留めの交渉結果（<see cref="ShellGeometry.ResolveDocked"/>・<see cref="ShellGeometry.NeedsApply"/>）。
/// <para>
/// 実機（Windows 11、1920x1080、拡大率100%、タスクバーは下で高さ48px）の shell.log の数値を
/// そのまま使う。<c>ABM_QUERYPOS</c> が下端を24px 詰めて返し、窓の下とタスクバーの間に
/// 24px の隙間ができた。
/// </para>
/// </summary>
public class DockNegotiationTests
{
    private static readonly RECT Monitor = new() { left = 0, top = 0, right = 1920, bottom = 1080 };

    /// <summary>実機の作業領域。タスクバー（48px）を除いた bottom=1032。</summary>
    private static readonly RECT Work = new() { left = 0, top = 0, right = 1920, bottom = 1032 };

    private static RECT Rect(int left, int top, int right, int bottom) =>
        new() { left = left, top = top, right = right, bottom = bottom };

    // ------------------------------------------------------------------
    // ResolveDocked
    // ------------------------------------------------------------------

    [Fact]
    public void 実機の数値でQUERYPOSが下を詰めても上下は提案の作業領域を採る()
    {
        var proposed = ShellGeometry.ProposeRect(Monitor, Work, DockEdge.Right, width: 290);
        Assert.Equal(Rect(1630, 0, 1920, 1032), proposed);

        // 実機の QUERYPOS後 =(1630,0,1920,1008)
        var queried = Rect(1630, 0, 1920, 1008);

        var docked = ShellGeometry.ResolveDocked(proposed, queried, DockEdge.Right, 290, out var trimmed);

        Assert.Equal(Rect(1630, 0, 1920, 1032), docked);
        Assert.True(trimmed, "上下を詰められたことが記録用に分かる");
    }

    [Fact]
    public void 上下を詰められなかったときは記録の印を立てない()
    {
        var proposed = Rect(1630, 0, 1920, 1032);

        var docked = ShellGeometry.ResolveDocked(proposed, proposed, DockEdge.Right, 290, out var trimmed);

        Assert.Equal(proposed, docked);
        Assert.False(trimmed);
    }

    [Fact]
    public void 同じ辺に他のAppBarが居て左右を譲られたときは左右だけ返事を採る()
    {
        // 右の辺に幅 100 の別の AppBar が先に居る。QUERYPOS は右端を 1820 に詰めて返す
        var proposed = Rect(1630, 0, 1920, 1032);
        var queried = Rect(1630, 0, 1820, 1008);

        var docked = ShellGeometry.ResolveDocked(proposed, queried, DockEdge.Right, 290, out _);

        // 右端は返事の 1820、そこから自分の幅 290 を切り出す。上下は提案のまま
        Assert.Equal(Rect(1530, 0, 1820, 1032), docked);
    }

    [Fact]
    public void 左に寄せたときも上下は提案を採り左右は返事を採る()
    {
        var proposed = ShellGeometry.ProposeRect(Monitor, Work, DockEdge.Left, width: 290);
        var queried = Rect(60, 0, 1920, 1008); // 左に幅 60 の別の AppBar

        var docked = ShellGeometry.ResolveDocked(proposed, queried, DockEdge.Left, 290, out var trimmed);

        Assert.Equal(Rect(60, 0, 350, 1032), docked);
        Assert.True(trimmed);
    }

    [Fact]
    public void 上に別のAppBarが居て作業領域の上端が下がっていても提案の上端を採る()
    {
        // 上に高さ 40 の AppBar。rcWork はすでにそのぶん削れている（top=40）
        var work = Rect(0, 40, 1920, 1032);
        var proposed = ShellGeometry.ProposeRect(Monitor, work, DockEdge.Right, width: 290);
        var queried = Rect(1630, 40, 1920, 1008);

        var docked = ShellGeometry.ResolveDocked(proposed, queried, DockEdge.Right, 290, out _);

        Assert.Equal(Rect(1630, 40, 1920, 1032), docked);
    }

    [Fact]
    public void 返事の左右が空なら左右も提案のままにする()
    {
        var proposed = Rect(1630, 0, 1920, 1032);
        var queried = Rect(0, 0, 0, 0);

        var docked = ShellGeometry.ResolveDocked(proposed, queried, DockEdge.Right, 290, out _);

        Assert.Equal(proposed, docked);
    }

    // ------------------------------------------------------------------
    // NeedsApply（同じ矩形なら SETPOS も移動も繰り返さない）
    // ------------------------------------------------------------------

    [Fact]
    public void まだ確定していなければ反映する()
    {
        Assert.True(ShellGeometry.NeedsApply(null, Rect(1630, 0, 1920, 1032), Rect(1630, 0, 1920, 1032), 1));
    }

    [Fact]
    public void 確定済みと同じで窓もそこに居れば反映しない()
    {
        var next = Rect(1630, 0, 1920, 1032);

        Assert.False(ShellGeometry.NeedsApply(next, next, next, 1));
    }

    [Fact]
    public void 窓が1ピクセルだけずれていても丸めの範囲なら反映しない()
    {
        var next = Rect(1630, 0, 1920, 1032);
        var window = Rect(1630, 0, 1920, 1033);

        Assert.False(ShellGeometry.NeedsApply(next, next, window, 1));
    }

    [Fact]
    public void 交渉の結果が変わったら反映する()
    {
        // 作業領域の変更や表示設定の変更で、上下や左右が本当に変わった
        var confirmed = Rect(1630, 0, 1920, 1032);
        var window = confirmed;

        Assert.True(ShellGeometry.NeedsApply(confirmed, Rect(1630, 0, 1920, 1080), window, 1));
        Assert.True(ShellGeometry.NeedsApply(confirmed, Rect(1530, 0, 1820, 1032), window, 1));
        Assert.True(ShellGeometry.NeedsApply(confirmed, Rect(1630, 40, 1920, 1032), window, 1));
    }

    [Fact]
    public void 窓が確定した位置から離れていたら反映する()
    {
        var confirmed = Rect(1630, 0, 1920, 1032);

        Assert.True(ShellGeometry.NeedsApply(confirmed, confirmed, Rect(1920, 0, 2210, 1032), 1));
    }

    [Fact]
    public void 窓の矩形が取れなかったときは反映する()
    {
        var confirmed = Rect(1630, 0, 1920, 1032);

        Assert.True(ShellGeometry.NeedsApply(confirmed, confirmed, null, 1));
    }

    [Fact]
    public void 実機の再交渉3回は最初の1回だけ反映される()
    {
        // 実機ではピン1回で Reposition が3回走った。どれも同じ提案・同じ返事
        var proposed = Rect(1630, 0, 1920, 1032);
        var queried = Rect(1630, 0, 1920, 1008);

        RECT? confirmed = null;
        RECT? window = Rect(1630, 0, 1920, 1032); // ピン前の窓（実機の MoveTo前 GetWindowRect）
        var applied = 0;

        for (var i = 0; i < 3; i++)
        {
            var next = ShellGeometry.ResolveDocked(proposed, queried, DockEdge.Right, 290, out _);

            if (!ShellGeometry.NeedsApply(confirmed, next, window, 1)) continue;

            applied++;
            confirmed = next;
            window = next;
        }

        Assert.Equal(1, applied);
    }
}
