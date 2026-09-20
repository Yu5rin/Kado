using System.Runtime.InteropServices;

namespace SlideinaCalendar.App.Shell;

/// <summary>
/// シェル統合に要る Win32 の呼び出し（要件書 7章）。
/// <para>
/// WPF だけでは、トレイ常駐・ワークエリアの確保・グローバルホットキーのどれも
/// できない。WinForms の <c>NotifyIcon</c> を借りる手もあるが、<c>UseWindowsForms</c>
/// を立てると WPF と名前が衝突する型が大量に入るので、要るものだけ自分で呼ぶ。
/// </para>
/// </summary>
internal static class NativeMethods
{
    // ------------------------------------------------------------------
    // AppBar（SHAppBarMessage）
    // ------------------------------------------------------------------

    internal const int ABM_NEW = 0x00000000;
    internal const int ABM_REMOVE = 0x00000001;
    internal const int ABM_QUERYPOS = 0x00000002;
    internal const int ABM_SETPOS = 0x00000003;

    internal const int ABE_LEFT = 0;
    internal const int ABE_RIGHT = 2;

    /// <summary>全画面のアプリが出入りしたときに来る。一時的に退避する。</summary>
    internal const int ABN_FULLSCREENAPP = 0x00000002;

    /// <summary>位置を変えろと言われたとき。タスクバーが動いたなど。</summary>
    internal const int ABN_POSCHANGED = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public int uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;

        public int Width => right - left;

        public int Height => bottom - top;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

    // ------------------------------------------------------------------
    // ワークエリア（異常終了からの復旧）
    // ------------------------------------------------------------------

    internal const int SPI_SETWORKAREA = 0x002F;
    internal const int SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(
        int uiAction, int uiParam, ref RECT pvParam, int fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    internal const int MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    internal const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    // ------------------------------------------------------------------
    // トレイ（Shell_NotifyIcon）
    // ------------------------------------------------------------------

    internal const int NIM_ADD = 0x00000000;
    internal const int NIM_MODIFY = 0x00000001;
    internal const int NIM_DELETE = 0x00000002;

    internal const int NIF_MESSAGE = 0x00000001;
    internal const int NIF_ICON = 0x00000002;
    internal const int NIF_TIP = 0x00000004;
    internal const int NIF_INFO = 0x00000010;

    internal const int NIIF_INFO = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr LoadImage(
        IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    internal const uint IMAGE_ICON = 1;
    internal const uint LR_LOADFROMFILE = 0x00000010;
    internal const uint LR_DEFAULTSIZE = 0x00000040;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// 実行ファイルに埋め込まれたアイコンを取り出す。
    /// <para>
    /// 配布物は単一ファイルなので、隣に .ico を置いておくことができない。
    /// <c>ApplicationIcon</c> で焼き込んだものを、exe から直に引く。
    /// </para>
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint ExtractIconEx(
        string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    // ------------------------------------------------------------------
    // ホットキー・メッセージ
    // ------------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_NOREPEAT = 0x4000;

    internal const int WM_HOTKEY = 0x0312;
    internal const int WM_DISPLAYCHANGE = 0x007E;

    internal const int WM_LBUTTONUP = 0x0202;
    internal const int WM_RBUTTONUP = 0x0205;
    internal const int WM_LBUTTONDBLCLK = 0x0203;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);
}
