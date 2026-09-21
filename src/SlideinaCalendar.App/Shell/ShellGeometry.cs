using SlideinaCalendar.Presentation.Settings;
using static SlideinaCalendar.App.Shell.NativeMethods;

namespace SlideinaCalendar.App.Shell;

/// <summary>
/// <see cref="System.Windows.Window"/> や Win32 の呼び出しを挟まない、シェルまわりの
/// 純粋な計算。
/// <para>
/// 入力から出力が一意に決まる部分だけをここへ引き剥がしてある。<see cref="ShellController"/>
/// や <see cref="AppBarHost"/> の本体は実際に Win32 を呼ぶので Windows でしか検査できないが、
/// ここは <c>Window</c> に依存しないのでテストできる。
/// </para>
/// </summary>
internal static class ShellGeometry
{
    /// <summary>
    /// 画面の外に置いたときの左端。休止位置（<paramref name="resting"/>）を基準にする。
    /// <para>
    /// モニタの取り違えや倍率のずれがあっても、寄せている辺の向こう側にしか行かない
    /// （不具合1）。寄せている辺にタスクバーがあるなどで休止位置がモニタ端より内側の
    /// ときは、モニタ端まで出す。
    /// </para>
    /// </summary>
    internal static double OffScreenLeft(DockEdge edge, double resting, double width, RECT screen, double scale) =>
        edge == DockEdge.Left
            ? Math.Min(resting, screen.left / scale) - width
            : Math.Max(resting + width, screen.right / scale);

    /// <summary>1 DIP 未満のずれでは動かさない。丸め誤差だけで毎回位置を書き換えない（不具合2）。</summary>
    internal static bool ShouldMove(double value, double current) => Math.Abs(value - current) >= 1;

    /// <summary>
    /// AppBar へ提案する矩形。上下は作業領域、左右はモニタ全体から取る（不具合2）。
    /// <para>
    /// 上下までモニタ全体を提案すると、タスクバー分の切り詰めを Windows 任せにする
    /// ことになり、ピン留めした瞬間に少し動く。左右まで作業領域から取ってしまうと、
    /// 登録中に再交渉が走るたびに、自分が削った帯のぶん内側へ押し込まれていく。
    /// </para>
    /// </summary>
    internal static RECT ProposeRect(RECT monitor, RECT work, DockEdge edge, int width)
    {
        var rc = monitor;
        rc.top = work.top;
        rc.bottom = work.bottom;

        return SliceWidth(rc, edge, width);
    }

    /// <summary>
    /// 矩形から、寄せている辺の側に自分の幅ぶんだけ切り出す。
    /// <para><c>ABM_QUERYPOS</c> への提案と、返ってきた矩形からの切り出しの両方で使う。</para>
    /// </summary>
    internal static RECT SliceWidth(RECT rect, DockEdge edge, int width)
    {
        if (edge == DockEdge.Left) rect.right = rect.left + width;
        else rect.left = rect.right - width;

        return rect;
    }
}
