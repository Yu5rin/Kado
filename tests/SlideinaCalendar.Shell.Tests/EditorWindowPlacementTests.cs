using SlideinaCalendar.App.Editing;
using SlideinaCalendar.Presentation.Settings;

namespace SlideinaCalendar.Shell.Tests;

/// <summary>
/// <see cref="EditorWindowPlacement"/> ―― 編集ウィンドウを帯の隣へ置く、純粋な位置計算。
/// <para>
/// 実機の報告：スライド／ピン留めのときに編集ウィンドウが帯へ重なって出ていた。
/// 帯の外側の隣・上端を揃えて出すはずが、正しく計算できているかをここで確かめる。
/// さらに追加の要望で「隙間なく・上をぴったり揃える」ことになったので、
/// 帯にぴったりくっつくこと・上端が一致することを確かめる形にしている。
/// </para>
/// </summary>
public class EditorWindowPlacementTests
{
    // フル HD、作業領域はモニタと同じとする（タスクバーは今回の計算に関係ない）
    private static readonly EditorWindowPlacement.Rect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void 左に寄せた帯の右隣に隙間なく上端を揃えて出す()
    {
        // 画面左端（x=0）に幅270pxの帯
        var band = new EditorWindowPlacement.Rect(0, 0, 270, 1080);

        var (left, top) = EditorWindowPlacement.NextToBand(
            Screen, band, DockEdge.Left, editorWidth: 440, editorHeight: 300);

        // 帯の右端（270）にぴったりくっつく（隙間なし）
        Assert.Equal(band.Right, left);
        // 帯の上端（0）にぴったり揃う
        Assert.Equal(band.Top, top);
    }

    [Fact]
    public void 右に寄せた帯の左隣に隙間なく上端を揃えて出す()
    {
        // 画面右端に幅270pxの帯（左端は 1920-270=1650）
        var band = new EditorWindowPlacement.Rect(1650, 0, 270, 1080);

        var (left, top) = EditorWindowPlacement.NextToBand(
            Screen, band, DockEdge.Right, editorWidth: 440, editorHeight: 300);

        // 編集ウィンドウの右端が帯の左端（1650）にぴったりくっつく
        // ＝ 左端は 1650 - 440 = 1210
        Assert.Equal(1210, left);
        Assert.Equal(band.Top, top);
    }

    [Fact]
    public void 帯が上端0でなくても帯の上端にぴったり揃える()
    {
        // 複数モニタでモニタの原点が0でない・帯が画面の途中から始まる場合
        var band = new EditorWindowPlacement.Rect(2000, 120, 270, 900);
        var screen = new EditorWindowPlacement.Rect(1920, 0, 1920, 1080);

        var (_, top) = EditorWindowPlacement.NextToBand(
            screen, band, DockEdge.Left, editorWidth: 440, editorHeight: 300);

        Assert.Equal(band.Top, top);
    }

    [Fact]
    public void 画面右端をはみ出す場合は画面の中へ収め直す()
    {
        // 帯が画面のほぼ右端にあり、右隣に440px置くと画面からはみ出す
        var band = new EditorWindowPlacement.Rect(1700, 0, 270, 1080);

        var (left, _) = EditorWindowPlacement.NextToBand(
            Screen, band, DockEdge.Left, editorWidth: 440, editorHeight: 300);

        // 本来なら 1700+270=1970 だが、画面幅1920に収める
        // ＝ 右端が画面右端に一致する位置（1920-440=1480）
        Assert.Equal(1480, left);
        Assert.True(left + 440 <= Screen.Right, "画面の右へはみ出してはいけない");
    }

    [Fact]
    public void 画面下端をはみ出す場合は画面の中へ収め直す()
    {
        // 帯の上端が画面の下のほうにあり、そのまま置くと編集ウィンドウが画面の下へはみ出す
        var band = new EditorWindowPlacement.Rect(0, 900, 270, 180);

        var (_, top) = EditorWindowPlacement.NextToBand(
            Screen, band, DockEdge.Left, editorWidth: 440, editorHeight: 300);

        // 本来なら帯の上端（900）に揃うが、画面高さ1080に収める（1080-300=780）
        Assert.Equal(780, top);
        Assert.True(top + 300 <= Screen.Bottom, "画面の下へはみ出してはいけない");
    }

    [Fact]
    public void 編集ウィンドウが画面より大きいときは例外を投げず画面の左上に収める()
    {
        var band = new EditorWindowPlacement.Rect(0, 0, 270, 1080);
        var tinyScreen = new EditorWindowPlacement.Rect(0, 0, 300, 200);

        var (left, top) = EditorWindowPlacement.NextToBand(
            tinyScreen, band, DockEdge.Left, editorWidth: 440, editorHeight: 300);

        Assert.Equal(0, left);
        Assert.Equal(0, top);
    }

    [Fact]
    public void 左右どちらでも帯には重ならない()
    {
        foreach (var edge in new[] { DockEdge.Left, DockEdge.Right })
        {
            var band = edge == DockEdge.Left
                ? new EditorWindowPlacement.Rect(0, 0, 270, 1080)
                : new EditorWindowPlacement.Rect(1650, 0, 270, 1080);

            var (left, _) = EditorWindowPlacement.NextToBand(
                Screen, band, edge, editorWidth: 440, editorHeight: 300);

            var overlaps = left < band.Right && left + 440 > band.Left;
            Assert.False(overlaps, $"edge={edge} で帯と重なった: left={left}, band=({band.Left},{band.Right})");
        }
    }
}
