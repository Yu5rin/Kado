using System.Windows;
using System.Windows.Threading;
using Kado.Presentation.Settings;
using static Kado.App.Shell.NativeMethods;

namespace Kado.App.Shell;

/// <summary>
/// 画面端のホットゾーン（要件書 7.1）。
/// <para>
/// 画面端の細い帯にマウスが留まったらスライドインさせる。<b>当たっただけでは出さない。</b>
/// 画面端は他の操作でも通るので、少し留まったときだけにする（誤爆防止）。
/// </para>
/// <para>
/// <b>透明なウィンドウは置かない。</b>要件書は <c>WS_EX_TRANSPARENT</c> の帯を
/// 常駐させて <c>WM_MOUSEMOVE</c> を拾う案だが、この拡張スタイルを立てた窓は
/// ヒットテストから外れるので、そもそもマウスのメッセージが来ない。立てなければ
/// 来るが、今度は画面端にあるウィンドウの閉じるボタンやスクロールバーを
/// 押せなくなる。低レベルのマウスフックは、常駐して全入力を見る形になり、
/// ウイルス対策ソフトに止められることがある。
/// </para>
/// <para>
/// そこでカーソルの座標を short い間隔で見るだけにした。窓を置かないので
/// 他の操作を一切邪魔せず、止められる心配もない。
/// </para>
/// </summary>
internal sealed class EdgeHotZone : IDisposable
{
    /// <summary>
    /// 帯の幅（物理ピクセル）。
    /// <para>
    /// 要件書は3〜5px だが、実機で「端に当てても出てこない」となった。高 DPI では
    /// 数ピクセルが目視でほとんど無いに等しく、当てたつもりで外していた。少し広げる。
    /// </para>
    /// </summary>
    private const int ZoneWidth = 8;

    /// <summary>カーソルを見にいく間隔。</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    /// <summary>出すまでに留まっている必要のある時間。短いと誤爆する。</summary>
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 出たあと、外れてから引っ込めるまでの猶予。
    /// <para>
    /// 端を掠めただけで消えると、押そうとしたボタンが逃げる。出すときより長く取る。
    /// </para>
    /// </summary>
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _timer;

    private DockEdge _edge = DockEdge.Right;
    private RECT _screen;
    private DateTime? _since;
    private bool _disposed;

    /// <summary>出ているあいだ見張る窓の矩形（物理ピクセル）。</summary>
    private RECT _window;

    /// <summary>いま見張っているのは、帯ではなく「窓から外れること」か。</summary>
    private bool _watchingLeave;

    /// <summary>外れてからの時間。</summary>
    private DateTime? _outside;

    /// <summary>留まったので出してほしい。</summary>
    public event EventHandler? Triggered;

    /// <summary>窓から外れたので引っ込めてほしい。</summary>
    public event EventHandler? Left;

    public EdgeHotZone()
    {
        // Background だと、他のアプリを操作しているあいだに後回しにされることがある。
        // 出てこない、という報告の元になっていた可能性がある
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = Tick };
        _timer.Tick += (_, _) => Check(DateTime.UtcNow);
    }

    /// <summary>いま張っているか。</summary>
    public bool IsArmed => _timer.IsEnabled;

    /// <summary>
    /// 帯を張る。
    /// <para>ピン留め中は張らない。常時出ているので、呼び出す口が要らない（要件書 2.2）。</para>
    /// </summary>
    /// <param name="edge">寄せる辺。</param>
    /// <param name="screen">
    /// 見張る画面（物理ピクセル）。
    /// <para>
    /// 張るときに1回だけ決める。毎回カーソルからモニタを引き直す作りにしていたが、
    /// 呼び出しが1つ増えるぶん失敗する余地があり、失敗すると黙って出てこなくなる。
    /// </para>
    /// </param>
    public void Arm(DockEdge edge, RECT screen)
    {
        if (_disposed) return;

        _edge = edge;
        _screen = screen;
        _since = null;
        _watchingLeave = false;
        _outside = null;
        _timer.Start();
    }

    /// <summary>
    /// 出したあと、カーソルが窓から外れるのを見張る。
    /// <para>
    /// 帯の見張りは止める。出ているあいだに帯を踏んでも、もう出すものが無い。
    /// </para>
    /// </summary>
    /// <param name="window">出ている窓の矩形（物理ピクセル）。</param>
    public void WatchLeaving(RECT window)
    {
        if (_disposed || !_timer.IsEnabled) return;

        _window = window;
        _watchingLeave = true;
        _outside = null;
        _since = null;
    }

    /// <summary>見張りを帯に戻す。引っ込めたあとに呼ぶ。</summary>
    public void WatchEdge()
    {
        _watchingLeave = false;
        _outside = null;
        _since = null;
    }

    /// <summary>帯を外す。</summary>
    public void Disarm()
    {
        _timer.Stop();
        _since = null;
        _watchingLeave = false;
        _outside = null;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _timer.Stop();
    }

    /// <summary>
    /// いまのカーソルを見て、留まっていれば知らせる。
    /// <para>時計を渡せるようにしてあるのは、間隔の判定を確かめられるようにするため。</para>
    /// </summary>
    internal void Check(DateTime now)
    {
        if (!GetCursorPos(out var point))
        {
            _since = null;
            _outside = null;
            return;
        }

        // 出ているあいだは、帯ではなく窓から外れるのを見る
        if (_watchingLeave)
        {
            CheckLeaving(point, now);
            return;
        }

        if (!IsInZone(point))
        {
            _since = null;
            return;
        }

        // 入ったばかり。まだ出さない
        if (_since is not { } since)
        {
            _since = now;
            return;
        }

        if (now - since < Dwell) return;

        // 一度出したら、いったん帯から離れるまで出し直さない
        _since = null;
        Triggered?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 窓から外れたか。
    /// <para>掠めただけで消えないよう、外れたまま少し置いてから知らせる。</para>
    /// </summary>
    private void CheckLeaving(POINT point, DateTime now)
    {
        if (Contains(_window, point))
        {
            _outside = null;
            return;
        }

        if (_outside is not { } since)
        {
            _outside = now;
            return;
        }

        if (now - since < LeaveDelay) return;

        _outside = null;
        Left?.Invoke(this, EventArgs.Empty);
    }

    private static bool Contains(RECT rect, POINT point) =>
        point.x >= rect.left && point.x < rect.right &&
        point.y >= rect.top && point.y < rect.bottom;

    /// <summary>
    /// 帯の中か。
    /// <para>
    /// 張るときに決めた画面の端で見る。仮想画面の端で見ると、真ん中の画面では
    /// 二度と出なくなる。
    /// </para>
    /// <para>縦は画面の中に居ればよい。上下の端まで使えたほうが当てやすい。</para>
    /// </summary>
    private bool IsInZone(POINT point)
    {
        if (_screen.Width <= 0) return false;

        if (point.y < _screen.top || point.y > _screen.bottom) return false;

        return _edge == DockEdge.Left
            ? point.x <= _screen.left + ZoneWidth
            : point.x >= _screen.right - 1 - ZoneWidth;
    }
}
