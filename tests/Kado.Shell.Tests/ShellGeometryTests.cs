using Kado.App.Shell;
using Kado.Presentation.Settings;
using static Kado.App.Shell.NativeMethods;

namespace Kado.Shell.Tests;

/// <summary>
/// <see cref="ShellGeometry"/> ―― <c>Window</c> や Win32 の呼び出しを挟まない、
/// シェルまわりの純粋な計算だけを検査する。
/// <para>
/// 実際に Win32 を呼ぶ部分（<c>ShellController</c>・<c>AppBarHost</c> の本体）は
/// Windows でしか検査できない。ここで確かめるのは、入力から出力が一意に決まる
/// 部分だけ。
/// </para>
/// </summary>
public class ShellGeometryTests
{
    // ------------------------------------------------------------------
    // OffScreenLeft（不具合1：スライドが反対側から出てくる）
    // ------------------------------------------------------------------

    private static readonly RECT Screen = new() { left = 0, top = 0, right = 1920, bottom = 1080 };

    [Fact]
    public void 左に寄せているときは左端の外へ出す()
    {
        // 休止位置がちょうどモニタの左端（DIP）のとき
        var offScreen = ShellGeometry.OffScreenLeft(DockEdge.Left, resting: 0, width: 320, Screen, scale: 1.0);

        // モニタ左端（0）から、窓の幅ぶんさらに外側
        Assert.Equal(-320, offScreen);
    }

    [Fact]
    public void 右に寄せているときは右端の外へ出す()
    {
        var offScreen = ShellGeometry.OffScreenLeft(DockEdge.Right, resting: 1600, width: 320, Screen, scale: 1.0);

        // モニタ右端（1920）そのものが左端になる（右端から出るので窓の左端は画面の右端）
        Assert.Equal(1920, offScreen);
    }

    [Fact]
    public void 休止位置がモニタ端より内側でもモニタ端まで出す()
    {
        // タスクバーなどの都合で休止位置がモニタ左端より内側（DIP で 8）にずれていても、
        // 反対側（画面の内側）へは絶対に出ない。モニタの左端を基準にする
        var offScreen = ShellGeometry.OffScreenLeft(DockEdge.Left, resting: 8, width: 320, Screen, scale: 1.0);

        Assert.Equal(-320, offScreen);
        Assert.True(offScreen < 0, "反対側（画面の内側）に出てはいけない");
    }

    [Fact]
    public void 休止位置が右エッジでモニタ端より内側でもモニタ端まで出す()
    {
        var offScreen = ShellGeometry.OffScreenLeft(DockEdge.Right, resting: 1580, width: 320, Screen, scale: 1.0);

        Assert.Equal(1920, offScreen);
        Assert.True(offScreen >= Screen.right, "反対側（画面の内側）に出てはいけない");
    }

    [Fact]
    public void 倍率が掛かっていても物理ピクセルからDIPへ正しく戻す()
    {
        // 200% 表示（scale=2.0）。モニタは物理ピクセルで 3840 幅
        var screen = new RECT { left = 0, top = 0, right = 3840, bottom = 2160 };

        var offScreen = ShellGeometry.OffScreenLeft(DockEdge.Right, resting: 1600, width: 320, screen, scale: 2.0);

        // 3840 / 2.0 = 1920（DIP）が右端。窓の左端はそこに一致する
        Assert.Equal(1920, offScreen);
    }

    [Fact]
    public void 左右どちらでもモニタの向こう側にしか出ない()
    {
        // 寄せている辺と逆側に出ることが構造的に無いことを、境界値をいくつか変えて確かめる
        foreach (var resting in new[] { -50, 0, 8, 100 })
        {
            var offScreen = ShellGeometry.OffScreenLeft(DockEdge.Left, resting, width: 320, Screen, scale: 1.0);
            Assert.True(offScreen <= Screen.left, $"resting={resting} で右側へ出た: {offScreen}");
        }

        foreach (var resting in new[] { 1500, 1600, 1912, 2000 })
        {
            var offScreen = ShellGeometry.OffScreenLeft(DockEdge.Right, resting, width: 320, Screen, scale: 1.0);
            Assert.True(offScreen >= Screen.right, $"resting={resting} で左側へ出た: {offScreen}");
        }
    }

    // ------------------------------------------------------------------
    // ShouldMove（不具合2：ピン留めで少し動いてから留まる）
    // ------------------------------------------------------------------

    [Fact]
    public void 差が1DIP未満のずれでは動かさない()
    {
        Assert.False(ShellGeometry.ShouldMove(100.4, 100.0));
        Assert.False(ShellGeometry.ShouldMove(99.6, 100.0));
    }

    [Fact]
    public void 差が1DIP以上ずれたら動かす()
    {
        Assert.True(ShellGeometry.ShouldMove(101.0, 100.0));
        Assert.True(ShellGeometry.ShouldMove(98.9, 100.0));
    }

    [Fact]
    public void ずれが無ければ動かさない()
    {
        Assert.False(ShellGeometry.ShouldMove(100.0, 100.0));
    }

    // ------------------------------------------------------------------
    // ProposeRect / SliceWidth（不具合2：AppBar へ提案する矩形の組み立て）
    // ------------------------------------------------------------------

    private static readonly RECT Monitor = new() { left = 0, top = 0, right = 1920, bottom = 1080 };

    /// <summary>タスクバーが下に 40px あるモニタを想定した作業領域。</summary>
    private static readonly RECT Work = new() { left = 0, top = 0, right = 1920, bottom = 1040 };

    [Fact]
    public void 上下は作業領域左右はモニタ全体から取る()
    {
        var rc = ShellGeometry.ProposeRect(Monitor, Work, DockEdge.Left, width: 320);

        // 上下：作業領域（タスクバーぶんが削られている）
        Assert.Equal(Work.top, rc.top);
        Assert.Equal(Work.bottom, rc.bottom);

        // 左右：モニタ全体を基準に、寄せている辺から幅ぶん切り出す
        Assert.Equal(Monitor.left, rc.left);
        Assert.Equal(Monitor.left + 320, rc.right);
    }

    [Fact]
    public void 右に寄せたときも上下は作業領域左右はモニタ全体から取る()
    {
        var rc = ShellGeometry.ProposeRect(Monitor, Work, DockEdge.Right, width: 320);

        Assert.Equal(Work.top, rc.top);
        Assert.Equal(Work.bottom, rc.bottom);

        Assert.Equal(Monitor.right, rc.right);
        Assert.Equal(Monitor.right - 320, rc.left);
    }

    [Fact]
    public void 左右まで作業領域から取ってしまわない()
    {
        // 左右にも AppBar など他社製の帯があって作業領域が左右から削られている想定。
        // ここを作業領域基準にすると、登録中に再交渉が走るたびに自分が削った帯の
        // ぶん内側へ押し込まれていく（要件書どおり、左右は必ずモニタ全体を使う）
        var narrowedWork = new RECT { left = 40, top = 0, right = 1880, bottom = 1040 };

        var rc = ShellGeometry.ProposeRect(Monitor, narrowedWork, DockEdge.Left, width: 320);

        Assert.Equal(Monitor.left, rc.left);
        Assert.NotEqual(narrowedWork.left, rc.left);
    }

    // ------------------------------------------------------------------
    // RevealLeft（開く演出：幅から Left を求める式、寄せている辺による左右の反転）
    // ------------------------------------------------------------------

    [Fact]
    public void 左に寄せているときはLeftを動かさない()
    {
        // 幅がどう変わっても、左に寄せているときは定位置の Left のまま
        foreach (var width in new[] { 1.0, 100.0, 200.0, 320.0 })
        {
            var left = ShellGeometry.RevealLeft(DockEdge.Left, restingLeft: 0, restingWidth: 320, width);
            Assert.Equal(0, left);
        }
    }

    [Fact]
    public void 右に寄せているときは右端を固定してLeftを詰める()
    {
        // 定位置：Left=1600, Width=320 → 右端は 1920
        const double restingLeft = 1600;
        const double restingWidth = 320;

        // 幅が 1 のとき、Left は右端ぎりぎり（1920 - 1 = 1919）
        var atStart = ShellGeometry.RevealLeft(DockEdge.Right, restingLeft, restingWidth, width: 1);
        Assert.Equal(1919, atStart);

        // 幅が定位置まで戻れば、Left も定位置に戻る
        var atRest = ShellGeometry.RevealLeft(DockEdge.Right, restingLeft, restingWidth, width: restingWidth);
        Assert.Equal(restingLeft, atRest);
    }

    [Fact]
    public void 右に寄せているときは幅が狭いほどLeftが右へ寄る()
    {
        // 右端（restingLeft + restingWidth）を固定したまま幅を広げていくので、
        // Left は単調に減っていく（左へ伸びていく）はず
        const double restingLeft = 1600;
        const double restingWidth = 320;

        var narrow = ShellGeometry.RevealLeft(DockEdge.Right, restingLeft, restingWidth, width: 50);
        var wide = ShellGeometry.RevealLeft(DockEdge.Right, restingLeft, restingWidth, width: 200);

        Assert.True(narrow > wide, "幅が狭いほうが Left は右（大きい値）にあるはず");

        // どちらでも右端は動かない
        Assert.Equal(restingLeft + restingWidth, narrow + 50);
        Assert.Equal(restingLeft + restingWidth, wide + 200);
    }

    [Fact]
    public void SliceWidthは寄せている辺の側だけ切り出す()
    {
        var left = ShellGeometry.SliceWidth(Monitor, DockEdge.Left, 320);
        Assert.Equal(Monitor.left, left.left);
        Assert.Equal(Monitor.left + 320, left.right);
        Assert.Equal(Monitor.top, left.top);
        Assert.Equal(Monitor.bottom, left.bottom);

        var right = ShellGeometry.SliceWidth(Monitor, DockEdge.Right, 320);
        Assert.Equal(Monitor.right, right.right);
        Assert.Equal(Monitor.right - 320, right.left);
    }

    // ------------------------------------------------------------------
    // TryGuardWindowPos（ピン留め時に一瞬右へ飛ぶ不具合のBガード）
    // ------------------------------------------------------------------

    /// <summary>ピン留め済みの窓（左端、幅320×高さ1040）を想定した確定値。</summary>
    private static readonly RECT Confirmed = new() { left = 0, top = 0, right = 320, bottom = 1040 };

    [Fact]
    public void 確定値からずれた移動は押し戻す()
    {
        // シェルが窓を右へ280（自分の幅ぶん）押し出そうとした形
        var changed = ShellGeometry.TryGuardWindowPos(
            Confirmed, x: 280, y: 0, cx: 320, cy: 1040, flags: 0,
            out var gx, out var gy, out var gcx, out var gcy);

        Assert.True(changed);
        Assert.Equal(Confirmed.left, gx);
        Assert.Equal(Confirmed.top, gy);
        Assert.Equal(Confirmed.Width, gcx);
        Assert.Equal(Confirmed.Height, gcy);
    }

    [Fact]
    public void 確定値と同じ移動は変えない()
    {
        var changed = ShellGeometry.TryGuardWindowPos(
            Confirmed, x: 0, y: 0, cx: 320, cy: 1040, flags: 0,
            out _, out _, out _, out _);

        Assert.False(changed);
    }

    [Fact]
    public void SWP_NOMOVEが立っていれば位置がずれていても触らない()
    {
        var changed = ShellGeometry.TryGuardWindowPos(
            Confirmed, x: 280, y: 0, cx: 320, cy: 1040, flags: SWP_NOMOVE,
            out var gx, out var gy, out _, out _);

        // 位置は Windows が動かす気が無いので、ガードも触らない
        Assert.False(changed);
        Assert.Equal(280, gx);
        Assert.Equal(0, gy);
    }

    [Fact]
    public void SWP_NOSIZEが立っていれば大きさがずれていても触らない()
    {
        var changed = ShellGeometry.TryGuardWindowPos(
            Confirmed, x: 0, y: 0, cx: 200, cy: 500, flags: SWP_NOSIZE,
            out _, out _, out var gcx, out var gcy);

        Assert.False(changed);
        Assert.Equal(200, gcx);
        Assert.Equal(500, gcy);
    }

    [Fact]
    public void 位置だけずれていれば大きさは書き換えない()
    {
        var changed = ShellGeometry.TryGuardWindowPos(
            Confirmed, x: 280, y: 0, cx: 320, cy: 1040, flags: 0,
            out _, out _, out var gcx, out var gcy);

        Assert.True(changed);
        Assert.Equal(320, gcx);
        Assert.Equal(1040, gcy);
    }

    [Fact]
    public void SWP_NOMOVEとSWP_NOSIZEが両方立っていれば何もしない()
    {
        var changed = ShellGeometry.TryGuardWindowPos(
            Confirmed, x: 280, y: 10, cx: 200, cy: 500, flags: SWP_NOMOVE | SWP_NOSIZE,
            out var gx, out var gy, out var gcx, out var gcy);

        Assert.False(changed);
        Assert.Equal(280, gx);
        Assert.Equal(10, gy);
        Assert.Equal(200, gcx);
        Assert.Equal(500, gcy);
    }
}
