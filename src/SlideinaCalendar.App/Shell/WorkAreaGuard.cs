using System.IO;
using SlideinaCalendar.Data;
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
/// <para>
/// 印は2つに持っている。本来は <see cref="DockPlacementStore"/>（データベース）だが、
/// データベースが壊れて開けないと、それより前の段階では読み書きできない。
/// そのときのために、<c>%LocalAppData%\SlideinaCalendar\</c> の小さな空ファイルにも
/// 同じ印を持ち、データベースを開く前にこちらだけで確かめられるようにしてある。
/// </para>
/// </summary>
public static class WorkAreaGuard
{
    /// <summary>
    /// 前回の異常終了を検知し、ワークエリアを戻す（DB 版）。
    /// <para>データベースを開いたあとに一度だけ呼ぶ。</para>
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
    /// 前回の異常終了を検知し、ワークエリアを戻す（DB を介さない版）。
    /// <para>
    /// <see cref="RecoverIfNeeded(DockPlacementStore)"/> と役割は同じだが、データベースを
    /// 一切使わない。データベースが壊れて開けなくなっていた場合の最後の砦なので、
    /// データベースを開く<b>前</b>――起動のいちばん早い段階で呼ぶ。
    /// </para>
    /// </summary>
    /// <returns>戻したら true。何もしなければ false。</returns>
    public static bool RecoverIfNeeded()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めなくても、DB 版の印が生きていれば次でそちらが拾う
            return false;
        }

        RestoreAllMonitors();
        MarkReserved(false);
        return true;
    }

    /// <summary>
    /// ワークエリアを削っている／いないを、DB を介さない印にも書く。
    /// <para>
    /// <b>削る前に true を、戻したあとに false を書く。</b><c>AppBarHost</c> が
    /// <c>DockPlacementStore.SetWorkAreaReserved</c> でしている DB 版の印と同じ順序。
    /// 書けなくても支障はない――そのときは DB 版の印だけが頼りになる。
    /// </para>
    /// </summary>
    public static void MarkReserved(bool reserved)
    {
        try
        {
            if (reserved)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                File.WriteAllText(MarkerPath, string.Empty);
            }
            else if (File.Exists(MarkerPath))
            {
                File.Delete(MarkerPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 書けなくても、DB 版の印が生きていれば次の起動で拾える
        }
    }

    /// <summary>DB を介さない印の置き場所。データベースと同じフォルダ。</summary>
    private static string MarkerPath => Path.Combine(
        Path.GetDirectoryName(CalendarDatabase.DefaultPath)!, "workarea.reserved");

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
