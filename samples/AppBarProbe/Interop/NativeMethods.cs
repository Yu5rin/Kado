using System.Runtime.InteropServices;

namespace AppBarProbe.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;

    public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom})";
}

[StructLayout(LayoutKind.Sequential)]
internal struct APPBARDATA
{
    public int cbSize;
    public IntPtr hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public RECT rc;
    public IntPtr lParam;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

/// <summary>AppBar を配置する辺。</summary>
internal enum AppBarEdge : uint
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}

/// <summary>
/// AppBar の検証に必要な Win32 API。
/// <para>本番では SlideinaCalendar.Shell に集約するが、プロトタイプでは単体で完結させる。</para>
/// </summary>
internal static class NativeMethods
{
    // ---- SHAppBarMessage のメッセージ ----
    public const uint ABM_NEW = 0x00000000;
    public const uint ABM_REMOVE = 0x00000001;
    public const uint ABM_QUERYPOS = 0x00000002;
    public const uint ABM_SETPOS = 0x00000003;
    public const uint ABM_WINDOWPOSCHANGED = 0x00000009;

    // ---- AppBar からの通知（uCallbackMessage の wParam） ----
    public const int ABN_STATECHANGE = 0x0000000;
    public const int ABN_POSCHANGED = 0x0000001;
    public const int ABN_FULLSCREENAPP = 0x0000002;
    public const int ABN_WINDOWARRANGE = 0x0000003;

    // ---- SystemParametersInfo ----
    public const uint SPI_GETWORKAREA = 0x0030;
    public const uint SPI_SETWORKAREA = 0x002F;
    public const uint SPIF_UPDATEINIFILE = 0x01;
    public const uint SPIF_SENDCHANGE = 0x02;

    // ---- SetWindowPos ----
    public static readonly IntPtr HWND_TOP = new(0);
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    public const uint WM_WINDOWPOSCHANGED = 0x0047;

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(
        uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>現在のワークエリア（プライマリモニタ）を取得する。</summary>
    public static RECT GetWorkArea()
    {
        var rect = default(RECT);
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref rect, 0);
        return rect;
    }

    /// <summary>
    /// ワークエリアを書き戻す。<paramref name="notify"/> を立てると全ウィンドウへ変更が通知される。
    /// </summary>
    public static bool SetWorkArea(RECT rect, bool notify = true)
        => SystemParametersInfo(SPI_SETWORKAREA, 0, ref rect, notify ? SPIF_SENDCHANGE : 0);

    /// <summary>ウィンドウが乗っているモニタの矩形とワークエリア。</summary>
    public static MONITORINFO GetMonitorInfoFor(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        return info;
    }
}
