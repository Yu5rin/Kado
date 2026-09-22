using SlideinaCalendar.Presentation.Settings;

namespace SlideinaCalendar.App.Editing;

/// <summary>
/// 予定・タスク・カレンダーの編集ウィンドウを、帯（スライド／ピン留め）の隣へ
/// 出す位置を計算する。
/// <para>
/// <see cref="System.Windows.Window"/> や Win32 の呼び出しを挟まない、純粋な計算
/// だけをここへ切り出してある（<c>SlideinaCalendar.App.Shell.ShellGeometry</c> と
/// 同じ考え方）。<c>DialogEditorPresenter</c> 側は、ここで出た値をそのまま
/// <c>Window.Left</c>／<c>Window.Top</c> へ入れるだけにする。
/// </para>
/// <para>
/// 実機の報告：ウィンドウモードなら親の中央（<c>CenterOwner</c>）でよいが、
/// スライドやピン留めのときは親が細い帯なので、真ん中に出すと帯に重なって
/// 中身が隠れる。帯の外側の隣・上端を揃えて出す。
/// </para>
/// </summary>
internal static class EditorWindowPlacement
{
    /// <summary>
    /// 帯と編集ウィンドウの隙間（DIP）。
    /// <para>実機の報告は「8〜12px程度」。その中間を採る。</para>
    /// </summary>
    internal const double Gap = 10;

    /// <summary>
    /// 上端を帯の上端からわずかに下げる量（DIP）。
    /// <para>
    /// 実機の画像では帯の上端ぴったりではなく数pxだけ下にずれた位置が「望む位置」
    /// として示されていた（「揃えるか 4〜8px 下げる程度」でよいとのこと）。その
    /// 範囲の中間を採る。
    /// </para>
    /// </summary>
    internal const double TopOffset = 6;

    /// <summary>画面や帯の矩形（左・上・幅・高さ、DIP）。</summary>
    internal readonly record struct Rect(double Left, double Top, double Width, double Height)
    {
        internal double Right => Left + Width;

        internal double Bottom => Top + Height;
    }

    /// <summary>
    /// 帯の隣・上端を揃えた位置を出す。
    /// <para>
    /// 左に寄せている帯なら、編集ウィンドウの左端を「帯の右端＋隙間」に置く。
    /// 右に寄せているなら、編集ウィンドウの右端を「帯の左端－隙間」に置く
    /// （＝左端は「帯の左端－隙間－編集ウィンドウの幅」）。どちらも
    /// <paramref name="screen"/>（帯が乗っているモニタの作業領域）からはみ出さない
    /// よう収め直す。
    /// </para>
    /// </summary>
    /// <param name="screen">帯が乗っているモニタの矩形（作業領域）。</param>
    /// <param name="band">帯（本体ウィンドウ）の矩形。</param>
    /// <param name="edge">帯を寄せている辺。</param>
    /// <param name="editorWidth">編集ウィンドウの幅。</param>
    /// <param name="editorHeight">編集ウィンドウの高さ。</param>
    internal static (double Left, double Top) NextToBand(
        Rect screen, Rect band, DockEdge edge, double editorWidth, double editorHeight)
    {
        var left = edge == DockEdge.Left
            ? band.Right + Gap
            : band.Left - Gap - editorWidth;

        var top = band.Top + TopOffset;

        return (
            Clamp(left, screen.Left, screen.Right - editorWidth),
            Clamp(top, screen.Top, screen.Bottom - editorHeight));
    }

    /// <summary>
    /// 範囲に収める。<paramref name="max"/> が <paramref name="min"/> を下回るとき
    /// （編集ウィンドウが画面そのものより大きいとき）は <paramref name="min"/> を返す。
    /// <c>Math.Clamp</c> は <c>min &gt; max</c> だと例外を投げるので、ここで避けておく。
    /// </summary>
    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Clamp(value, min, max);
}
