using SlideinaCalendar.Presentation.Settings;
using static SlideinaCalendar.App.Shell.NativeMethods;

namespace SlideinaCalendar.App.Shell;

/// <summary>
/// ワークエリアの復旧（要件書 2.3）。
/// <para>
/// <b><c>ABM_REMOVE</c> を呼ばずにプロセスが落ちると、ワークエリアが削られたまま
/// 残り、ユーザーのデスクトップが壊れる。</b>最大化したウィンドウが画面いっぱいに
/// ならなくなり、原因も分からない。アプリを消しても直らない。
/// </para>
/// <para>
/// 起動時に「前回は削ったまま終わった」と分かったら、<c>SPI_SETWORKAREA</c> で
/// モニタ全体に戻す。戻しすぎても、タスクバーが自分でワークエリアを取り直す。
/// </para>
/// </summary>
public static class WorkAreaGuard
{
    /// <summary>
    /// 前回の異常終了を検知し、ワークエリアを戻す。
    /// <para>起動時に一度だけ呼ぶ。</para>
    /// </summary>
    /// <returns>戻したら true。何もしなければ false。</returns>
    public static bool RecoverIfNeeded(DockPlacementStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (!store.WasWorkAreaLeftReserved()) return false;

        RestoreAllMonitors();
        store.SetWorkAreaReserved(false);
        return true;
    }

    /// <summary>
    /// 全モニタのワークエリアをモニタ全体に戻す。
    /// <para>
    /// タスクバーぶんまで広げてしまうが、タスクバーは自分で取り直すので元に収まる。
    /// 削られたまま残るよりはるかにましで、副作用も自然に消える。
    /// </para>
    /// </summary>
    private static void RestoreAllMonitors()
    {
        var monitors = new List<IntPtr>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr handle, IntPtr _, ref RECT _, IntPtr _) =>
            {
                monitors.Add(handle);
                return true;
            },
            IntPtr.Zero);

        foreach (var monitor in monitors)
        {
            var info = new MONITORINFOEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>(),
                szDevice = string.Empty,
            };

            if (!GetMonitorInfo(monitor, ref info)) continue;

            var full = info.rcMonitor;
            SystemParametersInfo(SPI_SETWORKAREA, 0, ref full, SPIF_SENDCHANGE);
        }
    }
}
