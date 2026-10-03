using Kado.App.Shell;
using static Kado.App.Shell.NativeMethods;

namespace Kado.Shell.Tests;

/// <summary>
/// 起動時に、控えてあった窓の位置（DIP）を物理ピクセルへ戻す倍率は、<b>控えた位置の画面</b>のもの。
/// <para>
/// 窓を出す前は、窓の倍率が主画面のものを指す。主画面でない側（倍率の違う別の画面）に
/// 置いていた窓を主画面の倍率で戻すと、本来の場所からずれて、乗っていない・違う場所に乗っている、
/// と判断される。
/// </para>
/// </summary>
public class SavedWindowResolveTests
{
    private static RECT Rect(int left, int top, int right, int bottom) =>
        new() { left = left, top = top, right = right, bottom = bottom };

    /// <summary>ノート PC：物理 2880x1800、150%（DIP では 1920x1200）。主画面。</summary>
    private static readonly ScreenInfo Laptop =
        new(Rect(0, 0, 2880, 1800), Rect(0, 0, 2880, 1752), IsPrimary: true, Dpi: 144);

    /// <summary>外付け：物理 1920x1080、100%。ノート PC の右隣（物理 x=2880 から）。</summary>
    private static readonly ScreenInfo External =
        new(Rect(2880, 0, 4800, 1080), Rect(2880, 0, 4800, 1032), IsPrimary: false, Dpi: 96);

    private static readonly ScreenInfo[] Both = [Laptop, External];

    [Fact]
    public void 外付けの画面で控えた位置は_主画面の倍率ではなく外付けの倍率で戻す()
    {
        // 外付け（100%）の上で、物理 x=3000, y=100, 幅 1200, 高さ 800 に居た窓。DIP は倍率 1.0 なのでそのまま
        var resolved = ShellGeometry.ResolveSavedWindow(3000, 100, 1200, 800, Both, fallbackScale: 1.5);

        Assert.Equal(1, resolved.ScreenIndex);
        Assert.Equal(1.0, resolved.Scale);
        Assert.Equal(Rect(3000, 100, 4200, 900), resolved.Physical);

        // 主画面の倍率（1.5）で戻していた今までの値は、本来の場所から大きくずれ、外付けの外（右・下）へ飛び出していた
        var old = Rect(4500, 150, 6300, 1350);
        Assert.True(old.right > External.Monitor.right);
        Assert.True(old.bottom > External.Monitor.bottom);
        Assert.NotEqual(old, resolved.Physical);
    }

    [Fact]
    public void 戻した位置は十分に乗っているので_そのままにする()
    {
        var resolved = ShellGeometry.ResolveSavedWindow(3000, 100, 1200, 800, Both, 1.5);

        var fit = ShellGeometry.RestoreWindow(resolved.Physical, Both);

        Assert.False(fit.Relocated);
        Assert.Equal(1, fit.ScreenIndex);
    }

    [Fact]
    public void ノートPCの画面で控えた位置は_ノートPCの倍率で戻す()
    {
        // 150% の画面で、物理 (300,150)・1800x1200 に居た窓。DIP は 200,100・1200x800
        var resolved = ShellGeometry.ResolveSavedWindow(200, 100, 1200, 800, Both, fallbackScale: 1.5);

        Assert.Equal(0, resolved.ScreenIndex);
        Assert.Equal(1.5, resolved.Scale);
        Assert.Equal(Rect(300, 150, 2100, 1350), resolved.Physical);
    }

    [Fact]
    public void 倍率がみな同じなら_今までと同じ値になる()
    {
        ScreenInfo[] screens =
        [
            new(Rect(0, 0, 1920, 1080), Rect(0, 0, 1920, 1032), IsPrimary: true, Dpi: 96),
            new(Rect(-1024, 0, 0, 1280), Rect(-1024, 0, 0, 1280), IsPrimary: false, Dpi: 96),
        ];

        foreach (var (left, top, width, height) in new[]
        {
            (100.0, 50.0, 1200.0, 800.0), (-800.0, 10.0, 700.0, 600.0), (5000.0, 5000.0, 400.0, 300.0),
        })
        {
            var resolved = ShellGeometry.ResolveSavedWindow(left, top, width, height, screens, 1.0);

            // 今までは 窓の倍率で一律に直していた
            var expected = Rect(
                (int)Math.Round(left), (int)Math.Round(top),
                (int)Math.Round(left + width), (int)Math.Round(top + height));

            Assert.Equal(expected, resolved.Physical);
        }
    }

    [Fact]
    public void どの画面にも収まらなければ_窓の倍率で戻す_今までと同じ()
    {
        // もう無い画面に居た位置。どの画面の倍率で戻しても、その画面の外
        var resolved = ShellGeometry.ResolveSavedWindow(9000, 9000, 1200, 800, Both, fallbackScale: 1.5);

        Assert.Equal(-1, resolved.ScreenIndex);
        Assert.Equal(1.5, resolved.Scale);
        Assert.Equal(Rect(13500, 13500, 15300, 14700), resolved.Physical);

        // そのあとは今までどおり主画面へ置き直される
        var fit = ShellGeometry.RestoreWindow(resolved.Physical, Both);
        Assert.True(fit.Relocated);
        Assert.True(Both[fit.ScreenIndex].IsPrimary);
    }

    [Fact]
    public void 画面の一覧が空でも_窓の倍率で戻す()
    {
        var resolved = ShellGeometry.ResolveSavedWindow(100, 100, 800, 600, [], fallbackScale: 2.0);

        Assert.Equal(-1, resolved.ScreenIndex);
        Assert.Equal(Rect(200, 200, 1800, 1400), resolved.Physical);
    }

    [Fact]
    public void 画面をまたぐ位置で控えたときは_より多く乗る側の倍率で戻す()
    {
        // 外付けの左端付近。DIP 2900, 100 から 1200x800。外付けの倍率（1.0）なら全部が外付けに乗る。
        // ノート PC の倍率（1.5）だと 4350 から始まり外付けの右へ出る（ノート PC の画面には掛からない）
        var resolved = ShellGeometry.ResolveSavedWindow(2900, 100, 1200, 800, Both, 1.5);

        Assert.Equal(1, resolved.ScreenIndex);
        Assert.Equal(Rect(2900, 100, 4100, 900), resolved.Physical);
    }

    [Fact]
    public void 同じくらい収まる画面が複数あれば_窓の倍率と同じ画面を先にする()
    {
        // 小さな窓が、どちらの倍率でも自分の画面に収まる位置（原点付近は両画面とも…ではなく、
        // 100%（左）と 150%（右）の2画面が、DIP 600 付近で両方に収まるように並べた）
        ScreenInfo left = new(Rect(0, 0, 1920, 1080), Rect(0, 0, 1920, 1032), IsPrimary: true, Dpi: 96);
        ScreenInfo right = new(Rect(900, 0, 3780, 1800), Rect(900, 0, 3780, 1752), IsPrimary: false, Dpi: 144);
        ScreenInfo[] screens = [left, right];

        // DIP (700,100) 400x300：100% なら物理 (700..1100) で左に収まる。150% なら (1050..1650) で右に収まる
        var asWindow150 = ShellGeometry.ResolveSavedWindow(700, 100, 400, 300, screens, fallbackScale: 1.5);
        Assert.Equal(1, asWindow150.ScreenIndex);

        var asWindow100 = ShellGeometry.ResolveSavedWindow(700, 100, 400, 300, screens, fallbackScale: 1.0);
        Assert.Equal(0, asWindow100.ScreenIndex);
    }

    [Fact]
    public void 潰れた大きさや不正な倍率でも落ちない()
    {
        var zero = ShellGeometry.ResolveSavedWindow(100, 100, 0, 0, Both, 1.0);
        Assert.Equal(-1, zero.ScreenIndex);

        var badScale = ShellGeometry.ResolveSavedWindow(100, 100, 800, 600, Both, fallbackScale: 0);
        Assert.True(badScale.Physical.Width > 0);
        Assert.True(badScale.Scale > 0);
    }

    [Fact]
    public void 記録の行には換算に使った倍率が残る()
    {
        var resolved = ShellGeometry.ResolveSavedWindow(3000, 100, 1200, 800, Both, 1.5);
        var fit = ShellGeometry.RestoreWindow(resolved.Physical, Both);

        var line = ShellGeometry.FormatWindowRestoreLine(resolved.Physical, Both, fit, resolved);

        Assert.StartsWith("startup-restore mode=Window", line);
        Assert.Contains("換算倍率=1.00(画面#1)", line);

        // 引数を渡さない呼び方は、今までと同じ行
        Assert.DoesNotContain("換算倍率", ShellGeometry.FormatWindowRestoreLine(resolved.Physical, Both, fit));
    }
}
