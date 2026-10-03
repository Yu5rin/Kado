using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using static Kado.App.Shell.NativeMethods;

namespace Kado.App.Shell;

/// <summary>
/// トレイ常駐（要件書 7.4）。
/// <para>
/// 閉じるボタンで消えるのではなくトレイに入る。メニューから表示・同期・設定・終了。
/// </para>
/// <para>
/// WPF に通知領域の口は無い。WinForms の <c>NotifyIcon</c> を借りると
/// <c>UseWindowsForms</c> が要り、WPF と同じ名前の型が大量に入って衝突する
/// （実際にぶつかった）。そこで <c>Shell_NotifyIcon</c> を直に呼び、受け皿として
/// 画面に出さないウィンドウを1つだけ持つ。
/// </para>
/// <para>
/// <b>受け皿は、メッセージ専用（<c>HWND_MESSAGE</c>）ではなく、見えないトップレベル窓。</b>
/// Explorer が再起動したときにタスクバーが全窓へ送る <c>TaskbarCreated</c> は、メッセージ専用の窓には
/// 届かない。届かないとアイコンが消えたまま戻らず、窓を呼び戻す入口が無くなる。
/// 受けたらアイコンを置き直す。起動時に置けなかったときも、間をおいてやり直す。
/// </para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>トレイからの通知を受け取るメッセージ。WM_APP より後ろなら何でもよい。</summary>
    private const int TrayMessage = 0x0400 + 1024;

    private readonly HwndSource _sink;
    private readonly ContextMenu _menu;
    private readonly string _tooltip;
    private readonly System.Windows.Threading.DispatcherTimer _retry = new();

    /// <summary>Explorer が起動し直したときに全窓へ送られるメッセージの番号。0 なら登録できなかった。</summary>
    private readonly int _taskbarCreated;

    private IntPtr _icon;
    private bool _added;
    private bool _disposed;
    private int _attempts;

    /// <summary>左クリックまたはダブルクリックされた。</summary>
    public event EventHandler? Activated;

    /// <summary>
    /// <see cref="ShowBalloon"/> で出したバルーンが押された。
    /// <para>通知の領域のバージョンを上げていない（既定）ので、<c>lParam</c> にメッセージがそのまま入る。</para>
    /// </summary>
    public event EventHandler? BalloonClicked;

    /// <summary>バルーンが押された（<c>NIN_BALLOONUSERCLICK</c> = <c>WM_USER + 5</c>）。</summary>
    private const int NIN_BALLOONUSERCLICK = 0x0400 + 5;

    /// <summary>トレイにアイコンが出ているか。出ていないあいだは、窓を隠すと呼び戻せない。</summary>
    public bool IsShown => _added;

    /// <summary>出ている／出ていないが変わった。</summary>
    public event EventHandler? ShownChanged;

    public TrayIcon(string tooltip, ContextMenu menu, string? iconPath = null)
    {
        ArgumentNullException.ThrowIfNull(menu);

        _menu = menu;
        _tooltip = tooltip;

        // 画面に出さない受け皿。メッセージを受けるためだけに作る。
        // メッセージ専用の窓（HWND_MESSAGE）にはしない。ブロードキャストの TaskbarCreated が届かない。
        // 親なしのトップレベルだが、WS_VISIBLE を付けないので見えず、WS_EX_TOOLWINDOW でタスクバーにも
        // Alt+Tab にも出ない。WS_EX_NOACTIVATE でフォーカスも取らない
        _sink = new HwndSource(new HwndSourceParameters("Kado.Tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)WS_POPUP),
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        });

        _sink.AddHook(OnMessage);
        _icon = LoadIcon(iconPath);

        _taskbarCreated = (int)RegisterWindowMessage("TaskbarCreated");

        _retry.Tick += (_, _) =>
        {
            _retry.Stop();
            TryAddOrRetry();
        };

        TryAddOrRetry();
    }

    /// <summary>
    /// アイコンを置く。置けなければ間をおいてやり直す。
    /// <para>ログオン直後は、Explorer の通知領域がまだ受け付けないことがある。</para>
    /// </summary>
    private void TryAddOrRetry()
    {
        if (_disposed || _added) return;

        if (TryAdd())
        {
            _attempts = 0;
            return;
        }

        _attempts++;
        _retry.Interval = TrayPolicy.RetryDelay(_attempts);
        _retry.Start();
    }

    private bool TryAdd()
    {
        var data = Data();
        data.uFlags = NIF_MESSAGE | NIF_TIP | (_icon == IntPtr.Zero ? 0 : NIF_ICON);
        data.uCallbackMessage = TrayMessage;
        data.hIcon = _icon;
        data.szTip = Trim(_tooltip, 127);

        var added = Shell_NotifyIcon(NIM_ADD, ref data);

        if (!added)
        {
            // 追加が時間切れで「失敗」を返しても、実際には置かれていることがある。
            // 変更が通れば置いてあるので、二重に置かずに済ませる
            var probe = data;
            added = Shell_NotifyIcon(NIM_MODIFY, ref probe);
        }

        SetShown(added);
        return added;
    }

    private void SetShown(bool shown)
    {
        if (_added == shown) return;

        _added = shown;
        ShownChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// バルーンで知らせる。
    /// <para>
    /// トーストを出すには AUMID の登録と COM アクティベーターが要る。非 MSIX 配布で
    /// それが用意できないときの受け皿（要件書 7.5）。
    /// </para>
    /// </summary>
    public void ShowBalloon(string title, string message)
    {
        if (!_added) return;

        var data = Data();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(message, 255);
        data.dwInfoFlags = NIIF_INFO;

        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _retry.Stop();

        if (_added)
        {
            var data = Data();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        _sink.RemoveHook(OnMessage);
        _sink.Dispose();
    }

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _sink.Handle,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Explorer が起動し直した。通知領域は作り直されてアイコンが消えているので、置き直す
        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            _retry.Stop();
            _attempts = 0;
            SetShown(false);
            TryAddOrRetry();
            return IntPtr.Zero;
        }

        if (msg != TrayMessage) return IntPtr.Zero;

        switch ((int)lParam)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                Activated?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case WM_RBUTTONUP:
                ShowMenu();
                handled = true;
                break;

            case NIN_BALLOONUSERCLICK:
                BalloonClicked?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// メニューをカーソルの位置に出す。
    /// <para>
    /// 受け皿のウィンドウは画面に無いので、<c>PlacementTarget</c> は使えない。
    /// カーソルの座標を直に指す。
    /// </para>
    /// </summary>
    private void ShowMenu()
    {
        if (!GetCursorPos(out var point)) return;

        // 押しっぱなしでないと閉じないのを避けるため、いったん前に出す
        SetForegroundWindow(_sink.Handle);

        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        _menu.HorizontalOffset = point.x;
        _menu.VerticalOffset = point.y;
        _menu.IsOpen = true;
    }

    /// <summary>
    /// アイコンを読む。
    /// <para>
    /// まず実行ファイルに焼き込んであるものを引く。配布物は単一ファイルなので、
    /// 隣に .ico を置いておくことができない。
    /// </para>
    /// <para>
    /// 引けなければ、開発中に走らせたときのために隣の .ico も見る。どちらも
    /// 駄目ならアイコン無しで出す（トレイには既定の四角が出るが、メニューは使える）。
    /// </para>
    /// </summary>
    private static IntPtr LoadIcon(string? path)
    {
        if (path is null && Environment.ProcessPath is { Length: > 0 } exe)
        {
            var small = new IntPtr[1];

            if (ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero) return small[0];
        }

        var file = path ?? Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

        if (!File.Exists(file)) return IntPtr.Zero;

        return LoadImage(IntPtr.Zero, file, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
    }

    /// <summary>
    /// 構造体の欄に入る長さへ切る。
    /// <para>あふれたまま渡すと <c>Shell_NotifyIcon</c> ごと失敗し、アイコンが出ない。</para>
    /// </summary>
    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
