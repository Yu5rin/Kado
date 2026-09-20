using System.Windows;
using System.Windows.Interop;
using SlideinaCalendar.Presentation.Settings;
using static SlideinaCalendar.App.Shell.NativeMethods;

namespace SlideinaCalendar.App.Shell;

/// <summary>
/// ドック（AppBar）（要件書 2章・7.2）。
/// <para>
/// <b>本プロジェクトで最も優先度の高い要件。</b>画面端に留めたとき、他のウィンドウを
/// 最大化しても重ならないこと。オーバーレイ（自動非表示）ではワークエリアが削られず
/// 重なりを避けられないので、ピンを押したときだけ AppBar を登録する。
/// </para>
/// <para>
/// <b><c>ABM_REMOVE</c> を呼ばずに落ちると、ワークエリアが削られたまま残って
/// デスクトップが壊れる。</b>安全装置は要件書 2.3 の3点。ここが受け持つのは
/// 「削る前に印を付け、戻したら消す」ところ。異常終了の検知と復旧は
/// <see cref="WorkAreaGuard"/>、二重起動の防止は <c>SingleInstance</c>。
/// </para>
/// </summary>
public sealed class AppBarHost : IDisposable
{
    /// <summary>AppBar からの通知を受け取るメッセージ。名前で確保する（要件書 7.2）。</summary>
    private static readonly uint CallbackMessage =
        RegisterWindowMessage("SlideinaCalendar.AppBar");

    private readonly Window _window;
    private readonly DockPlacementStore _store;

    private HwndSource? _source;
    private bool _registered;
    private bool _disposed;

    /// <summary>全画面のアプリが出ているあいだは退避する。</summary>
    private bool _steppedAside;

    public AppBarHost(Window window, DockPlacementStore store)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>いまワークエリアを削っているか。</summary>
    public bool IsDocked => _registered;

    /// <summary>いま留まっている辺と幅。</summary>
    public DockEdge Edge { get; private set; } = DockEdge.Right;

    /// <summary>いまの幅。</summary>
    public double Width { get; private set; } = DockPlacement.DefaultWidth;

    /// <summary>ドックが外れたとき（全画面アプリや失敗で）。画面側の見た目を戻すのに使う。</summary>
    public event EventHandler? Undocked;

    /// <summary>
    /// ピン留めする。ワークエリアを削り、そのぶん他のウィンドウが寄る。
    /// </summary>
    /// <returns>削れたら true。</returns>
    public bool Dock(DockEdge edge, double width)
    {
        if (_disposed) return false;

        Edge = edge;
        Width = width;

        if (Handle() is not { } hwnd) return false;

        if (!_registered)
        {
            // 削る前に印を付ける。逆にすると、その隙に落ちたときに記録が残らない
            _store.SetWorkAreaReserved(true);

            var data = Data(hwnd);
            data.uCallbackMessage = CallbackMessage;

            if (SHAppBarMessage(ABM_NEW, ref data) == IntPtr.Zero)
            {
                _store.SetWorkAreaReserved(false);
                return false;
            }

            _registered = true;
            _source?.AddHook(OnMessage);
        }

        Reposition();
        return true;
    }

    /// <summary>
    /// ピンを外す。ワークエリアを元に戻す。
    /// <para>落ちても残らないよう、<b>この呼び出しは何度行っても安全</b>にしてある。</para>
    /// </summary>
    public void Undock()
    {
        if (!_registered) return;

        _registered = false;

        if (Handle() is { } hwnd)
        {
            var data = Data(hwnd);
            SHAppBarMessage(ABM_REMOVE, ref data);
        }

        _source?.RemoveHook(OnMessage);

        // 戻したあとに印を消す。先に消すと、戻す途中で落ちたときに検知できない
        _store.SetWorkAreaReserved(false);
        _steppedAside = false;

        Undocked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 幅を変えたときに交渉し直す。
    /// <para>
    /// 他のウィンドウの再配置を伴うので、<b>ドラッグ中は呼ばない</b>。
    /// 確定したときと、手が止まったときだけ（要件書 7.2）。
    /// </para>
    /// </summary>
    public void Resize(double width)
    {
        Width = width;

        if (_registered) Reposition();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        Undock();
    }

    /// <summary>
    /// 位置を交渉して決める。
    /// <para>
    /// <c>ABM_QUERYPOS</c> が返す矩形は、タスクバーや他社製 AppBar との調整が
    /// 済んだ値。<b>希望した値のまま <c>ABM_SETPOS</c> に渡してはいけない</b>
    /// （要件書 7.2）。返ってきた矩形を見て、自分の幅ぶんだけ切り出す。
    /// </para>
    /// </summary>
    private void Reposition()
    {
        if (Handle() is not { } hwnd) return;

        var monitor = MonitorRect(hwnd);
        var width = (int)Math.Round(Width * Scale());

        var data = Data(hwnd);
        data.uEdge = Edge == DockEdge.Left ? ABE_LEFT : ABE_RIGHT;
        data.rc = monitor;

        if (Edge == DockEdge.Left) data.rc.right = data.rc.left + width;
        else data.rc.left = data.rc.right - width;

        SHAppBarMessage(ABM_QUERYPOS, ref data);

        // 調整後の矩形から、改めて自分の幅を切り出す
        if (Edge == DockEdge.Left) data.rc.right = data.rc.left + width;
        else data.rc.left = data.rc.right - width;

        SHAppBarMessage(ABM_SETPOS, ref data);

        MoveTo(data.rc);
    }

    /// <summary>決まった矩形にウィンドウを合わせる。</summary>
    private void MoveTo(RECT rect)
    {
        var scale = Scale();

        _window.Left = rect.left / scale;
        _window.Top = rect.top / scale;
        _window.Width = rect.Width / scale;
        _window.Height = rect.Height / scale;
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DISPLAYCHANGE)
        {
            // モニタの構成が変わった。留まっていた画面がもう無いかもしれない
            if (_registered) Reposition();
            return IntPtr.Zero;
        }

        if (msg != (int)CallbackMessage) return IntPtr.Zero;

        switch ((int)wParam)
        {
            // 全画面のアプリが出た。重なったままだと相手の邪魔になるので退避し、
            // 終わったら戻る（要件書 2.2）
            case ABN_FULLSCREENAPP:
                _steppedAside = lParam != IntPtr.Zero;
                _window.Topmost = !_steppedAside;
                break;

            // タスクバーが動いたなど。位置を取り直す
            case ABN_POSCHANGED:
                if (_registered) Reposition();
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>ウィンドウのハンドル。まだ出ていなければ null。</summary>
    private IntPtr? Handle()
    {
        _source ??= (HwndSource?)PresentationSource.FromVisual(_window);

        if (_source is null)
        {
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle == IntPtr.Zero) return null;

            _source = HwndSource.FromHwnd(handle);
        }

        return _source?.Handle;
    }

    private static APPBARDATA Data(IntPtr hwnd) => new()
    {
        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(),
        hWnd = hwnd,
    };

    /// <summary>このウィンドウが乗っているモニタの全体。ワークエリアではない。</summary>
    private static RECT MonitorRect(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>(),
            szDevice = string.Empty,
        };

        return GetMonitorInfo(monitor, ref info) ? info.rcMonitor : default;
    }

    /// <summary>
    /// 画面の倍率。
    /// <para>
    /// AppBar はピクセルで話すが、WPF の <c>Left</c> や <c>Width</c> は倍率を割った値。
    /// 混ぜると、高 DPI の画面で幅がずれる。
    /// </para>
    /// </summary>
    private double Scale() =>
        PresentationSource.FromVisual(_window)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
}
