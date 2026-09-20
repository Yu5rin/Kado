using System.Windows;
using System.Windows.Interop;
using static SlideinaCalendar.App.Shell.NativeMethods;

namespace SlideinaCalendar.App.Shell;

/// <summary>グローバルホットキーの種類。</summary>
public enum HotKeyKind
{
    /// <summary>サイドバーを呼び出す。</summary>
    Show,

    /// <summary>クイック入力に飛ぶ。</summary>
    QuickEntry,
}

/// <summary>
/// グローバルホットキー（要件書 7.4）。
/// <para>
/// 他のアプリを使っているあいだでも効かせるので、WPF の <c>InputBinding</c> では
/// 足りない。<c>RegisterHotKey</c> で OS に登録する。
/// </para>
/// <para>
/// <b>他のアプリと取り合いになる。</b>先に取られていれば登録は失敗するが、
/// そこで止めない。ホットキーが無くても本体は使えるので、黙って諦める。
/// </para>
/// </summary>
public sealed class GlobalHotKeys : IDisposable
{
    private readonly HwndSource _source;
    private readonly List<int> _registered = [];

    private bool _disposed;

    /// <summary>押された。</summary>
    public event EventHandler<HotKeyKind>? Pressed;

    private GlobalHotKeys(HwndSource source)
    {
        _source = source;
        _source.AddHook(OnMessage);
    }

    /// <summary>
    /// 既定の組み合わせで登録する。
    /// <para>
    /// Ctrl＋Alt＋C で呼び出し、Ctrl＋Alt＋N でクイック入力。単独キーや Win 併用は
    /// 他と衝突しやすいので避ける。
    /// </para>
    /// </summary>
    /// <returns>ウィンドウがまだ出ていなければ null。</returns>
    public static GlobalHotKeys? Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return null;

        if (HwndSource.FromHwnd(handle) is not { } source) return null;

        var keys = new GlobalHotKeys(source);

        const uint VK_C = 0x43;
        const uint VK_N = 0x4E;
        const uint Modifiers = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;

        keys.Register(HotKeyKind.Show, Modifiers, VK_C);
        keys.Register(HotKeyKind.QuickEntry, Modifiers, VK_N);

        return keys;
    }

    /// <summary>1つ登録する。取られていれば false。</summary>
    public bool Register(HotKeyKind kind, uint modifiers, uint key)
    {
        if (_disposed) return false;

        var id = (int)kind + 1;

        // 他のアプリに取られていたら諦める。本体はホットキー無しでも使える
        if (!RegisterHotKey(_source.Handle, id, modifiers, key)) return false;

        _registered.Add(id);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        foreach (var id in _registered) UnregisterHotKey(_source.Handle, id);

        _registered.Clear();
        _source.RemoveHook(OnMessage);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;

        var id = (int)wParam - 1;
        if (!Enum.IsDefined(typeof(HotKeyKind), id)) return IntPtr.Zero;

        Pressed?.Invoke(this, (HotKeyKind)id);
        handled = true;
        return IntPtr.Zero;
    }
}
