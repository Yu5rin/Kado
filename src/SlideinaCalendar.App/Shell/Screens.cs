using System.Runtime.InteropServices;
using System.Windows;
using static SlideinaCalendar.App.Shell.NativeMethods;

namespace SlideinaCalendar.App.Shell;

/// <summary>
/// モニタの矩形を引く。
/// <para>
/// <b>失敗をそのまま 0 の矩形として返さない。</b>引けなかったときに 0 を流すと、
/// 画面端の帯が「幅0の画面」を見張ることになって二度と反応せず、ドックは
/// 高さ0の枠を交渉しにいく。どちらも「押しても何も起きない」としか見えないので、
/// 原因から遠いところで悩むことになる。引けなければプライマリ画面で代用する。
/// </para>
/// </summary>
internal static class Screens
{
    /// <summary>
    /// 窓が乗っているモニタ全体（物理ピクセル）。ワークエリアではない。
    /// </summary>
    /// <param name="hwnd">窓のハンドル。まだ出ていなければ <c>IntPtr.Zero</c>。</param>
    /// <param name="scale">画面の倍率。代用するときに、DIP を物理ピクセルへ直すのに使う。</param>
    internal static RECT Of(IntPtr hwnd, double scale)
    {
        if (hwnd != IntPtr.Zero)
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                szDevice = string.Empty,
            };

            if (GetMonitorInfo(monitor, ref info) && info.rcMonitor.Width > 0) return info.rcMonitor;
        }

        return Primary(scale);
    }

    /// <summary>
    /// 窓が乗っているモニタの作業領域（物理ピクセル）。タスクバーなどを除いた領域。
    /// <para>
    /// <b>スライドとピンで矩形の出どころを揃えるために足した（不具合2）。</b>
    /// 以前はスライドが <c>SystemParameters.WorkArea</c>（メインディスプレイ固定）を
    /// 使い、ピンはモニタ全体を提案していた。会社のような複数モニタでメイン以外へ
    /// 寄せて使うと、この2つが指す場所がまるごと食い違う。
    /// </para>
    /// </summary>
    /// <param name="hwnd">窓のハンドル。まだ出ていなければ <c>IntPtr.Zero</c>。</param>
    /// <param name="scale">画面の倍率。代用するときに、DIP を物理ピクセルへ直すのに使う。</param>
    internal static RECT WorkOf(IntPtr hwnd, double scale)
    {
        if (hwnd != IntPtr.Zero)
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                szDevice = string.Empty,
            };

            if (GetMonitorInfo(monitor, ref info) && info.rcWork.Width > 0) return info.rcWork;
        }

        return PrimaryWork(scale);
    }

    /// <summary>プライマリ画面。モニタを引けなかったときの代用。</summary>
    private static RECT Primary(double scale)
    {
        if (scale <= 0) scale = 1.0;

        return new RECT
        {
            left = 0,
            top = 0,
            right = (int)Math.Round(SystemParameters.PrimaryScreenWidth * scale),
            bottom = (int)Math.Round(SystemParameters.PrimaryScreenHeight * scale),
        };
    }

    /// <summary>プライマリ画面の作業領域。<see cref="WorkOf"/> がモニタを引けなかったときの代用。</summary>
    private static RECT PrimaryWork(double scale)
    {
        if (scale <= 0) scale = 1.0;

        var work = SystemParameters.WorkArea;

        return new RECT
        {
            left = (int)Math.Round(work.Left * scale),
            top = (int)Math.Round(work.Top * scale),
            right = (int)Math.Round(work.Right * scale),
            bottom = (int)Math.Round(work.Bottom * scale),
        };
    }
}
