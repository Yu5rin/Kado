using System.Windows;
using System.Windows.Threading;
using SlideinaCalendar.Presentation.Settings;
using static SlideinaCalendar.App.Shell.NativeMethods;

namespace SlideinaCalendar.App.Shell;

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
public sealed class EdgeHotZone : IDisposable
{
    /// <summary>帯の幅（物理ピクセル）。要件書 7.1 の3〜5px。</summary>
    private const int ZoneWidth = 4;

    /// <summary>カーソルを見にいく間隔。</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    /// <summary>出すまでに留まっている必要のある時間。短いと誤爆する。</summary>
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _timer;

    private DockEdge _edge = DockEdge.Right;
    private DateTime? _since;
    private bool _disposed;

    /// <summary>留まったので出してほしい。</summary>
    public event EventHandler? Triggered;

    public EdgeHotZone()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Tick };
        _timer.Tick += (_, _) => Check(DateTime.UtcNow);
    }

    /// <summary>いま張っているか。</summary>
    public bool IsArmed => _timer.IsEnabled;

    /// <summary>
    /// 帯を張る。
    /// <para>ピン留め中は張らない。常時出ているので、呼び出す口が要らない（要件書 2.2）。</para>
    /// </summary>
    public void Arm(DockEdge edge)
    {
        if (_disposed) return;

        _edge = edge;
        _since = null;
        _timer.Start();
    }

    /// <summary>帯を外す。</summary>
    public void Disarm()
    {
        _timer.Stop();
        _since = null;
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
    /// 帯の中か。
    /// <para>
    /// モニタが複数あるときは、カーソルが乗っている画面の端で見る。仮想画面の端で
    /// 見ると、真ん中の画面では二度と出なくなる。
    /// </para>
    /// </summary>
    private bool IsInZone(POINT point)
    {
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>(),
            szDevice = string.Empty,
        };

        if (!GetMonitorInfo(monitor, ref info)) return false;

        var screen = info.rcMonitor;

        return _edge == DockEdge.Left
            ? point.x <= screen.left + ZoneWidth
            : point.x >= screen.right - 1 - ZoneWidth;
    }
}
