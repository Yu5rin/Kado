using System.Runtime.InteropServices;

namespace Kado.App.Shell;

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

    /// <summary>
    /// モニタの矩形を引く。
    /// <para>
    /// <b><c>EntryPoint</c> を省いてはいけない。</b>既定は ANSI 版に結び付くのに、
    /// <see cref="MONITORINFOEX"/> は Unicode で並ぶ（名前が 64 バイト）ので、
    /// <c>cbSize</c> が ANSI 版の想定と合わず、<b>いつ呼んでも false が返る</b>。
    /// 失敗は 0 の矩形として素通りするため、画面端の帯が張れずスライドが出てこない、
    /// ドックの高さが足りない、といった形で遠くに症状が出る。
    /// </para>
    /// </summary>
    [DllImport("user32.dll", SetLastError = true,
        CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    /// <summary>
    /// 2回押しと見なす間隔（ミリ秒）。既定は 500。
    /// <para>
    /// 「1回押しで済み、2回押しで編集」を両立させるのに要る。1回目を押した時点では
    /// 2回目が来るか分からないので、この時間だけ待ってから1回押しの扱いにする。
    /// 人が設定で変えられる値なので、決め打ちにせず OS に訊く。
    /// </para>
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDoubleClickTime();

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

    // ------------------------------------------------------------------
    // 窓の移動・大きさ変更（ピン留め時に一瞬右へ飛ぶ不具合の切り分け・ガード用）
    // ------------------------------------------------------------------

    /// <summary>
    /// 窓が動かされる・大きさが変わる直前。<b>窓の移動は必ずこれを通る。</b>
    /// まだ確定前なので、<c>lParam</c>（<see cref="WINDOWPOS"/>）を書き換えれば
    /// Windows 側の実際の移動先に反映される。
    /// </summary>
    internal const int WM_WINDOWPOSCHANGING = 0x0046;

    /// <summary>窓が動かされた・大きさが変わったあと（確定後、記録専用）。</summary>
    internal const int WM_WINDOWPOSCHANGED = 0x0047;

    /// <summary>システム設定が変わった。<c>wParam</c> に何が変わったかが入る。</summary>
    internal const int WM_SETTINGCHANGE = 0x001A;

    internal const int SWP_NOSIZE = 0x0001;
    internal const int SWP_NOMOVE = 0x0002;

    /// <summary>並び順だけ変える（位置・大きさ・手前へ出す操作はしない）。</summary>
    internal const int SWP_NOACTIVATE = 0x0010;

    /// <summary>Z オーダーはそのまま（<c>hWndInsertAfter</c> を無視する）。</summary>
    internal const int SWP_NOZORDER = 0x0004;

    /// <summary>所有者の窓の重なり順を変えない。</summary>
    internal const int SWP_NOOWNERZORDER = 0x0200;

    /// <summary>いちばん手前の並びへ入れる。</summary>
    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>
    /// 窓の並び順を決め直す。
    /// <para>
    /// <c>Window.Topmost</c> を true にしてあっても、隠して出し直したり、他のアプリが
    /// 前面を取ったりした拍子に、実際の並びがそのとおりでなくなることがある。
    /// 帯として出しているあいだは <b>出すたびにここで並びを入れ直す</b>。
    /// <c>SWP_NOACTIVATE</c> を付けるので、前面を奪って相手の入力を邪魔することはない。
    /// </para>
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public int flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    // ------------------------------------------------------------------
    // 前面の窓（不具合3：予定追加でスライドが閉じる対策）
    // ------------------------------------------------------------------

    /// <summary>
    /// いま前面にいる窓。<c>MessageBox</c> やファイル選択ダイアログのように
    /// WPF の <see cref="System.Windows.Window"/> ではない窓にも効く判定に使う。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ------------------------------------------------------------------
    // 実際の窓の矩形（切り分け用ログ）
    // ------------------------------------------------------------------

    /// <summary>
    /// 窓の実際の矩形（物理ピクセル）。
    /// <para>
    /// <c>Window.Left</c> は WPF がそう思っている値でしかない。実機で「反対側から
    /// 出る」原因を確かめるため、Windows 自身が答える値と突き合わせられるようにする。
    /// </para>
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // ------------------------------------------------------------------
    // モニタの DPI（起動時の記録用）
    // ------------------------------------------------------------------

    internal const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
