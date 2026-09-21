using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using static SlideinaCalendar.App.Shell.NativeMethods;

namespace SlideinaCalendar.App.Shell;

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
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>トレイからの通知を受け取るメッセージ。WM_APP より後ろなら何でもよい。</summary>
    private const int TrayMessage = 0x0400 + 1024;

    private readonly HwndSource _sink;
    private readonly ContextMenu _menu;

    private IntPtr _icon;
    private bool _added;
    private bool _disposed;

    /// <summary>左クリックまたはダブルクリックされた。</summary>
    public event EventHandler? Activated;

    public TrayIcon(string tooltip, ContextMenu menu, string? iconPath = null)
    {
        ArgumentNullException.ThrowIfNull(menu);

        _menu = menu;

        // 画面に出さない受け皿。メッセージを受けるためだけに作る
        _sink = new HwndSource(new HwndSourceParameters("SlideinaCalendar.Tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            // HWND_MESSAGE。デスクトップに乗らないので、タスクバーにも出ない
            ParentWindow = new IntPtr(-3),
        });

        _sink.AddHook(OnMessage);
        _icon = LoadIcon(iconPath);

        var data = Data();
        data.uFlags = NIF_MESSAGE | NIF_TIP | (_icon == IntPtr.Zero ? 0 : NIF_ICON);
        data.uCallbackMessage = TrayMessage;
        data.hIcon = _icon;
        data.szTip = Trim(tooltip, 127);

        _added = Shell_NotifyIcon(NIM_ADD, ref data);
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
