using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AppBarProbe.Interop;

namespace AppBarProbe;

/// <summary>
/// AppBar の登録・解除と、Explorer からの通知への追従。
/// <para>
/// 確かめたいのは1点だけ。<b>ピン留めすると画面端にワークエリアが確保され、
/// 他のウィンドウを最大化しても重ならないこと。</b>
/// </para>
/// <para>
/// 登録は <c>ABM_NEW</c> → <c>ABM_QUERYPOS</c> → <c>ABM_SETPOS</c> の順。
/// <c>ABM_QUERYPOS</c> が返す矩形はタスクバーや他社製 AppBar との調整後の値なので、
/// 希望値のまま <c>ABM_SETPOS</c> に渡してはいけない（要件書 7.2）。
/// </para>
/// </summary>
internal sealed class AppBarController : IDisposable
{
    /// <summary>
    /// Explorer からの通知を受け取るメッセージ ID。
    /// <c>RegisterWindowMessage</c> で確保するとシステム全体で一意になる。
    /// </summary>
    private static readonly uint CallbackMessage =
        NativeMethods.RegisterWindowMessage("SlideinaCalendar.AppBarProbe.Callback");

    private readonly Window _window;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _disposed;

    // ピン留め前のウィンドウ外観。解除時に戻す。
    private WindowStyle _styleBeforeDock;
    private ResizeMode _resizeModeBeforeDock;
    private bool _chromeChanged;

    /// <summary>ドック中の辺。</summary>
    public AppBarEdge Edge { get; set; } = AppBarEdge.Right;

    /// <summary>希望するドック幅（物理ピクセル）。</summary>
    public int DesiredWidth { get; set; } = 352;

    /// <summary>現在 AppBar として登録されているか。</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>状態が変わったときに呼ばれる。UI へのログ出力用。</summary>
    public event Action<string>? StatusChanged;

    public AppBarController(Window window) => _window = window;

    // ------------------------------------------------------------------
    // 登録・解除
    // ------------------------------------------------------------------

    /// <summary>ピン留め。ワークエリアを削って画面分割を成立させる。</summary>
    public void Register()
    {
        if (IsRegistered) return;

        EnsureHandle();

        // ドック中はタイトルバーと枠を外す（要件書 7.2）。
        // 枠があるとドロップシャドウの分だけ画面端との間に隙間が見える。
        ApplyDockedChrome();

        // 削る前のワークエリアを控えておく。異常終了時はこれを書き戻して復旧する。
        WorkAreaRecovery.MarkRegistered(NativeMethods.GetWorkArea());

        var data = CreateData();
        data.uCallbackMessage = CallbackMessage;

        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data) == IntPtr.Zero)
        {
            WorkAreaRecovery.MarkUnregistered();
            RestoreChrome();
            Report("ABM_NEW に失敗しました。");
            return;
        }

        IsRegistered = true;
        _source?.AddHook(WndProc);

        Reposition();
        Report($"AppBar を登録しました（辺: {Edge}、幅: {DesiredWidth}px）。他のウィンドウを最大化して重ならないことを確認してください。");
    }

    /// <summary>ピン解除。ワークエリアを元に戻す。</summary>
    public void Unregister()
    {
        if (!IsRegistered) return;

        var data = CreateData();
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);

        IsRegistered = false;
        _source?.RemoveHook(WndProc);
        WorkAreaRecovery.MarkUnregistered();
        RestoreChrome();

        Report("AppBar を解除しました。ワークエリアが戻ります。");
    }

    /// <summary>
    /// 例外ハンドラから呼ぶ緊急解除。
    /// <para>
    /// 落ちる寸前に呼ばれるので、ログも UI 更新もせず <c>ABM_REMOVE</c> だけを投げる。
    /// ここで例外を出すと元の例外を覆い隠してしまうため、全て握り潰す。
    /// </para>
    /// </summary>
    public void EmergencyUnregister()
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;

            var data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                hWnd = _hwnd,
            };
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
            IsRegistered = false;
            WorkAreaRecovery.MarkUnregistered();
        }
        catch
        {
            // 緊急時なので何が起きても飲み込む
        }
    }

    // ------------------------------------------------------------------
    // 配置
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>ABM_QUERYPOS</c> で調整後の矩形をもらい、<c>ABM_SETPOS</c> で確定してからウィンドウを移動する。
    /// </summary>
    public void Reposition()
    {
        if (!IsRegistered) return;

        var monitor = NativeMethods.GetMonitorInfoFor(_hwnd).rcMonitor;

        var data = CreateData();
        data.uEdge = (uint)Edge;
        data.rc = DesiredRect(monitor);

        // 1. 希望値を投げて、他の AppBar との調整結果を受け取る
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref data);

        // 2. 調整された辺を基準に、希望する厚みを取り直す
        //    （QUERYPOS は「この辺はここまでしか使えない」を返すだけで、厚みは自分で決める）
        switch (Edge)
        {
            case AppBarEdge.Left:
                data.rc.Right = data.rc.Left + DesiredWidth;
                break;
            case AppBarEdge.Right:
                data.rc.Left = data.rc.Right - DesiredWidth;
                break;
            case AppBarEdge.Top:
                data.rc.Bottom = data.rc.Top + DesiredWidth;
                break;
            case AppBarEdge.Bottom:
                data.rc.Top = data.rc.Bottom - DesiredWidth;
                break;
        }

        // 3. 確定させる。返ってきた矩形が実際に割り当てられた領域
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref data);

        var rc = data.rc;
        ApplyWindowBounds(rc);

        Report($"再配置しました: {rc}（{rc.Width}×{rc.Height}）");
    }

    /// <summary>希望するドック矩形。モニタの端いっぱいに寄せる。</summary>
    private RECT DesiredRect(RECT monitor) => Edge switch
    {
        AppBarEdge.Left => monitor with { Right = monitor.Left + DesiredWidth },
        AppBarEdge.Right => monitor with { Left = monitor.Right - DesiredWidth },
        AppBarEdge.Top => monitor with { Bottom = monitor.Top + DesiredWidth },
        _ => monitor with { Top = monitor.Bottom - DesiredWidth },
    };

    /// <summary>
    /// 割り当てられた矩形に、ウィンドウの<b>見た目</b>をぴったり合わせる。
    /// <para>
    /// ウィンドウ矩形にはドロップシャドウ用の不可視マージンが含まれるため、
    /// 素直に置くと画面端との間に隙間が空く。その分だけ外側に広げて配置する。
    /// 枠を外してマージンが無い場合は補正量が 0 になるので、二重に広げることはない。
    /// </para>
    /// </summary>
    private void ApplyWindowBounds(RECT target)
    {
        var left = target.Left;
        var top = target.Top;
        var width = target.Width;
        var height = target.Height;

        if (NativeMethods.TryGetShadowPadding(_hwnd, out var pad))
        {
            left -= pad.Left;
            top -= pad.Top;
            width += pad.Left + pad.Right;
            height += pad.Top + pad.Bottom;
        }

        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero, left, top, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>ドック中の外観にする。タイトルバーと枠を外し、リサイズを止める。</summary>
    private void ApplyDockedChrome()
    {
        if (_chromeChanged) return;

        _styleBeforeDock = _window.WindowStyle;
        _resizeModeBeforeDock = _window.ResizeMode;
        _chromeChanged = true;

        _window.WindowStyle = WindowStyle.None;
        _window.ResizeMode = ResizeMode.NoResize;
    }

    /// <summary>
    /// ピン留め前の外観に戻す。例外処理から呼ぶための公開版。
    /// <para>
    /// <see cref="EmergencyUnregister"/> は UI に触れないため枠を外したままになる。
    /// タイトルバーが無いとウィンドウを閉じられないので、UI スレッドに戻れる経路では
    /// これを呼んで外観だけ復元する。
    /// </para>
    /// </summary>
    public void RestoreChromeIfNeeded() => RestoreChrome();

    /// <summary>ピン留め前の外観に戻す。</summary>
    private void RestoreChrome()
    {
        if (!_chromeChanged) return;

        _window.WindowStyle = _styleBeforeDock;
        _window.ResizeMode = _resizeModeBeforeDock;
        _chromeChanged = false;
    }

    // ------------------------------------------------------------------
    // 通知の受信
    // ------------------------------------------------------------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch (wParam.ToInt32())
            {
                case NativeMethods.ABN_POSCHANGED:
                    // タスクバーや他の AppBar が動いた。自分の場所を取り直す。
                    Reposition();
                    handled = true;
                    break;

                case NativeMethods.ABN_FULLSCREENAPP:
                    // 全画面アプリの開始（lParam != 0）／終了。開始中は退避する。
                    var entering = lParam != IntPtr.Zero;
                    NativeMethods.SetWindowPos(
                        _hwnd,
                        entering ? NativeMethods.HWND_BOTTOM : NativeMethods.HWND_TOP,
                        0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                    Report(entering ? "全画面アプリを検知。最背面に退避しました。" : "全画面アプリが終了。復帰しました。");
                    handled = true;
                    break;

                case NativeMethods.ABN_WINDOWARRANGE:
                    handled = true;
                    break;
            }
        }
        else if (msg == (int)NativeMethods.WM_WINDOWPOSCHANGED && IsRegistered)
        {
            // ドック中に自前で動かされた場合、Explorer へ位置の変化を伝える
            var data = CreateData();
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_WINDOWPOSCHANGED, ref data);
        }

        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------

    private void EnsureHandle()
    {
        if (_source is not null) return;

        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd)
            ?? throw new InvalidOperationException("ウィンドウハンドルを取得できませんでした。");
    }

    private APPBARDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<APPBARDATA>(),
        hWnd = _hwnd,
        uEdge = (uint)Edge,
    };

    private void Report(string message) => StatusChanged?.Invoke(message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
    }
}
