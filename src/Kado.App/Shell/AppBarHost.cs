using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Kado.Presentation.Settings;
using static Kado.App.Shell.NativeMethods;

namespace Kado.App.Shell;

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
    /// <summary>
    /// AppBar からの通知を受け取るメッセージ。名前で確保する（要件書 7.2）。
    /// <para>
    /// 確保に失敗したら 0 が返る。0 のまま <c>ABM_NEW</c> に渡すと登録ごと失敗するので、
    /// そのときは決め打ちの番号を使う。
    /// </para>
    /// </summary>
    private static readonly uint CallbackMessage =
        RegisterWindowMessage("Kado.AppBar") is var id && id != 0 ? id : 0x0400 + 1025;

    private readonly Window _window;
    private readonly DockPlacementStore _store;

    private HwndSource? _hooked;
    private bool _registered;
    private bool _disposed;

    /// <summary>全画面のアプリが出ているあいだは退避する。</summary>
    private bool _steppedAside;

    /// <summary>
    /// <c>ABM_SETPOS</c> で確定した矩形（物理ピクセル）。
    /// <para>
    /// シェルが窓を押し出そうとしたとき（ピン留め時に一瞬右へ飛ぶ不具合）、
    /// ここへ押し戻す。<see cref="Reposition"/> で更新し、<see cref="Undock"/> で消す。
    /// </para>
    /// </summary>
    private RECT? _confirmed;

    /// <summary>
    /// 自分から <see cref="MoveTo"/> で動かしている最中か。
    /// <para>ガード（<see cref="OnMessage"/>）は、自分の移動には素通りさせる。</para>
    /// </summary>
    private bool _selfMove;

    /// <summary>
    /// ガードを一時的に外すか。
    /// <para>
    /// <c>ShellController.ApplyPinnedWidth</c> は、幅をつまんでいるあいだ
    /// <c>_confirmed</c> より先に窓を動かす（交渉は手が止まってからまとめて行う）。
    /// ここを外さないと、つまんでいる最中にガードが古い確定値へ押し戻してしまい、
    /// 「幅をつまんで変えられない」という直したばかりの不具合が再発する。
    /// <c>ShellController</c> が <c>ShellViewModel.IsResizing</c> と連動させる。
    /// </para>
    /// </summary>
    internal bool SuppressGuard { get; set; }

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
    /// 直前の失敗の理由。
    /// <para>
    /// 黙って諦めると「ピンを押しても何も起きない」としか見えない。何が起きたのかを
    /// 残して、画面に出せるようにしておく。
    /// </para>
    /// </summary>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// ピン留めする。ワークエリアを削り、そのぶん他のウィンドウが寄る。
    /// <para>
    /// <b>削れたかどうかを測ってはいけない。</b>作業領域の変更はすぐには行き渡らず、
    /// 頼んだ直後に測ると変わっていないように見える。実際には削れているのに
    /// 「譲りませんでした」と断る羽目になった。登録が通ったかどうかだけを見る。
    /// </para>
    /// </summary>
    /// <returns>登録できたら true。</returns>
    public bool Dock(DockEdge edge, double width)
    {
        LastFailure = null;

        if (_disposed) return false;

        Edge = edge;
        Width = width;

        if (Handle() is not { } hwnd)
        {
            LastFailure = "ウィンドウがまだ画面に出ていません。";
            return false;
        }

        if (!_registered)
        {
            // 削る前に印を付ける。逆にすると、その隙に落ちたときに記録が残らない
            _store.SetWorkAreaReserved(true);

            var data = Data(hwnd);
            data.uCallbackMessage = CallbackMessage;

            if (SHAppBarMessage(ABM_NEW, ref data) == IntPtr.Zero)
            {
                _store.SetWorkAreaReserved(false);
                LastFailure = "Windows が画面端の枠（AppBar）の登録を受け付けませんでした。";
                return false;
            }

            _registered = true;
            Hook(hwnd);
        }

        Reposition();
        return true;
    }

    /// <summary>通知を受け取る口を付ける。二重に付けないよう、付けた先を覚えておく。</summary>
    private void Hook(IntPtr hwnd)
    {
        if (_hooked is not null) return;

        _hooked = HwndSource.FromHwnd(hwnd);
        _hooked?.AddHook(OnMessage);
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

        if (_hooked is not null)
        {
            _hooked.RemoveHook(OnMessage);
            _hooked = null;
        }

        // 戻したあとに印を消す。先に消すと、戻す途中で落ちたときに検知できない
        _store.SetWorkAreaReserved(false);
        _steppedAside = false;
        _confirmed = null;

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

        var scale = Scale();
        var monitor = Screens.Of(hwnd, scale);

        // 上下は作業領域、左右はモニタ全体から取る（不具合2）。
        //
        // 上下までモニタ全体を提案すると、タスクバー分の切り詰めを Windows 任せに
        // することになり、ピン留めした瞬間に少し動く。左右まで作業領域から取って
        // しまうと、登録中に再交渉が走るたびに、自分が削った帯のぶん内側へ
        // 押し込まれていく。左右は必ずモニタ全体のままにすること
        var work = Screens.WorkOf(hwnd, scale);
        var width = (int)Math.Round(Width * scale);

        var data = Data(hwnd);
        data.uEdge = Edge == DockEdge.Left ? ABE_LEFT : ABE_RIGHT;
        data.rc = ShellGeometry.ProposeRect(monitor, work, Edge, width);

        ShellDiagnosticsLog.Write(
            $"Reposition edge={Edge} width={Width:F1} scale={scale:F2} " +
            $"monitor=({monitor.left},{monitor.top},{monitor.right},{monitor.bottom}) " +
            $"work=({work.left},{work.top},{work.right},{work.bottom}) " +
            $"QUERYPOS前=({data.rc.left},{data.rc.top},{data.rc.right},{data.rc.bottom})");

        SHAppBarMessage(ABM_QUERYPOS, ref data);

        ShellDiagnosticsLog.Write(
            $"Reposition QUERYPOS後=({data.rc.left},{data.rc.top},{data.rc.right},{data.rc.bottom})");

        // 調整後の矩形から、改めて自分の幅を切り出す
        data.rc = ShellGeometry.SliceWidth(data.rc, Edge, width);

        // ABM_SETPOS を呼ぶ前に、これから頼む値を確定値として控えておく。シェルが
        // 窓を押し出す動きは ABM_SETPOS の呼び出し自体の中で起きうるので、呼んだ
        // あとで控えたのでは間に合わない（そのあいだに来た WM_WINDOWPOSCHANGING を
        // ガードで拾えない）
        _confirmed = data.rc;

        SHAppBarMessage(ABM_SETPOS, ref data);

        // 呼んだ結果、値が変わっていれば確定値も合わせる
        _confirmed = data.rc;

        var systemWorkArea = SystemParameters.WorkArea;

        ShellDiagnosticsLog.Write(
            $"Reposition SETPOS後=({data.rc.left},{data.rc.top},{data.rc.right},{data.rc.bottom}) " +
            $"SystemParameters.WorkArea=({systemWorkArea.Left:F0},{systemWorkArea.Top:F0}," +
            $"{systemWorkArea.Right:F0},{systemWorkArea.Bottom:F0})");

        // ABM_SETPOS の直後・MoveTo の前。ここで GetWindowRect を取れば、
        // ABN_POSCHANGED が来た時点で窓がどこに居たか（シェルに押し出された直後の
        // 姿）が MoveTo に書き換えられる前に残る
        if (GetWindowRect(hwnd, out var beforeMove))
        {
            ShellDiagnosticsLog.Write(
                $"Reposition MoveTo前 GetWindowRect=({beforeMove.left},{beforeMove.top}," +
                $"{beforeMove.right},{beforeMove.bottom}) Left={_window.Left:F1}");
        }

        // 自分から動かすので、ガードは素通りさせる
        _selfMove = true;
        try
        {
            MoveTo(data.rc);
        }
        finally
        {
            _selfMove = false;
        }

        if (GetWindowRect(hwnd, out var actual))
        {
            ShellDiagnosticsLog.Write(
                $"Reposition GetWindowRect=({actual.left},{actual.top},{actual.right},{actual.bottom})");
        }
    }

    /// <summary>
    /// 決まった矩形にウィンドウを合わせる。
    /// <para>
    /// <b>1 DIP 未満のずれでは代入しない（不具合2）。</b>スライドとピンで矩形の
    /// 出どころが少し違うだけで丸め誤差ぶんズレることがあり、そのたびに全プロパティへ
    /// 代入すると「留めた瞬間にわずかに動く」形で見える。
    /// </para>
    /// </summary>
    private void MoveTo(RECT rect)
    {
        var scale = Scale();

        Set(Window.LeftProperty, rect.left / scale, _window.Left);
        Set(Window.TopProperty, rect.top / scale, _window.Top);
        Set(Window.WidthProperty, rect.Width / scale, _window.Width);
        Set(Window.HeightProperty, rect.Height / scale, _window.Height);

        void Set(DependencyProperty property, double value, double current)
        {
            if (ShellGeometry.ShouldMove(value, current)) _window.SetValue(property, value);
        }
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DISPLAYCHANGE)
        {
            // モニタの構成が変わった。留まっていた画面がもう無いかもしれない
            if (_registered) Reposition();
            return IntPtr.Zero;
        }

        // 窓の移動・大きさ変更は必ずここを通る。AppBar を登録しているあいだだけ、
        // 誰が・いつ・どこへ動かそうとしたかを記録し（A）、確定した矩形と違えば
        // 押し戻す（B、ピン留め時に一瞬右へ飛ぶ不具合のガード）
        if (_registered && (msg == WM_WINDOWPOSCHANGING || msg == WM_WINDOWPOSCHANGED))
        {
            HandleWindowPos(msg, lParam);
            return IntPtr.Zero;
        }

        if (msg == WM_SETTINGCHANGE && (int)wParam == SPI_SETWORKAREA)
        {
            ShellDiagnosticsLog.Write($"OnMessage WM_SETTINGCHANGE SPI_SETWORKAREA registered={_registered}");
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

    /// <summary>
    /// <c>WM_WINDOWPOSCHANGING</c> / <c>WM_WINDOWPOSCHANGED</c> の処理。
    /// <para>
    /// <b>記録（A）。</b>毎フレーム書くと重くログも埋もれるが、AppBar を登録している
    /// あいだだけなので実害は無い。ピンを外せばこの経路自体を通らなくなる。
    /// </para>
    /// <para>
    /// <b>ガード（B）。</b><c>WM_WINDOWPOSCHANGING</c> はまだ確定前なので、
    /// <paramref name="lParam"/> の <see cref="WINDOWPOS"/> を書き換えれば Windows 側の
    /// 実際の移動先に反映される。シェルが「削った帯に重なる非 Topmost の窓」を
    /// 押し出そうとする動きを、確定した矩形（<see cref="_confirmed"/>）へ打ち消す。
    /// 自分から動かしている最中（<see cref="_selfMove"/>）や、幅をつまんでいる最中
    /// （<see cref="SuppressGuard"/>）は素通りさせる。
    /// </para>
    /// </summary>
    private void HandleWindowPos(int msg, IntPtr lParam)
    {
        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);

        ShellDiagnosticsLog.Write(
            $"OnMessage {(msg == WM_WINDOWPOSCHANGING ? "WM_WINDOWPOSCHANGING" : "WM_WINDOWPOSCHANGED")} " +
            $"x={pos.x} y={pos.y} cx={pos.cx} cy={pos.cy} flags=0x{pos.flags:X4} " +
            $"selfMove={_selfMove} suppressGuard={SuppressGuard}");

        if (msg != WM_WINDOWPOSCHANGING) return;
        if (_confirmed is not { } confirmed) return;
        if (_selfMove || SuppressGuard) return;

        if (!ShellGeometry.TryGuardWindowPos(
                confirmed, pos.x, pos.y, pos.cx, pos.cy, pos.flags,
                out var guardedX, out var guardedY, out var guardedCx, out var guardedCy))
        {
            return;
        }

        ShellDiagnosticsLog.Write(
            $"OnMessage ガードで押し戻す x={pos.x}->{guardedX} y={pos.y}->{guardedY} " +
            $"cx={pos.cx}->{guardedCx} cy={pos.cy}->{guardedCy}");

        pos.x = guardedX;
        pos.y = guardedY;
        pos.cx = guardedCx;
        pos.cy = guardedCy;

        Marshal.StructureToPtr(pos, lParam, false);
    }

    /// <summary>
    /// ウィンドウのハンドル。まだ出ていなければ null。
    /// <para>
    /// <b>控えずに毎回引き直す。</b>枠の出し方（<c>WindowStyle</c>）を変えると
    /// ハンドルが作り直されることがあり、古いものを握っていると Windows への
    /// 頼みごとが黙って空振りする。
    /// </para>
    /// </summary>
    private IntPtr? Handle()
    {
        var handle = new WindowInteropHelper(_window).Handle;

        return handle == IntPtr.Zero ? null : handle;
    }

    private static APPBARDATA Data(IntPtr hwnd) => new()
    {
        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(),
        hWnd = hwnd,
    };

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
