using System.Windows;
using System.Windows.Interop;
using static Kado.App.Shell.NativeMethods;

namespace Kado.App.Shell;

/// <summary>
/// タスクバー（と Alt+Tab）に出す窓のアイコンを、exe に焼き込んだ .ico から直に渡す。
/// <para>
/// 窓の <c>Icon</c> は <c>/Assets/app.png</c> を指していて、ふだんは WPF がそこから
/// アイコンを作って渡している。ところが、リモートデスクトップやプロジェクターの抜き差しで
/// 表示の倍率（DPI）が変わると、WPF が作り直したアイコンが渡らず、タスクバーに
/// Windows の既定の絵（アイコンの無いアプリ）が出ることがあった（v1.2.1、最小化中に
/// 画面の構成が何度も変わったあと）。<c>ShowInTaskbar</c> を出し直したときも同じ形になり得る。
/// </para>
/// <para>
/// そこで、画面の構成や倍率が変わったあとと、タスクバーにボタンを出し直したあとに、
/// exe のアイコン（大・小）を <c>WM_SETICON</c> で渡し直す。exe の .ico は大小の寸法を
/// 揃えて持っているので、倍率が変わっても Windows がふさわしい方を選べる。
/// </para>
/// </summary>
internal static class TaskbarIcon
{
    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    // 渡したアイコンは、窓が使っているあいだ消してはいけない。一度取り出したものを
    // アプリが終わるまで持ち続ける（2つだけなので、消し忘れても漏れは増えない）
    private static IntPtr _large;
    private static IntPtr _small;

    /// <summary>
    /// <paramref name="window"/> に exe のアイコンを渡し直す。窓のハンドルがまだ無い、
    /// exe からアイコンを取り出せないときは何もしない（WPF が渡したもののまま）。
    /// </summary>
    public static void Refresh(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !EnsureLoaded()) return;

            SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, _large);
            SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, _small);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 見た目だけの話なので、失敗しても動きは止めない
            ShellDiagnosticsLog.Write($"タスクバーのアイコンを渡し直せなかった {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool EnsureLoaded()
    {
        if (_large != IntPtr.Zero && _small != IntPtr.Zero) return true;
        if (Environment.ProcessPath is not { Length: > 0 } exe) return false;

        var large = new IntPtr[1];
        var small = new IntPtr[1];

        if (ExtractIconEx(exe, 0, large, small, 1) == 0 || large[0] == IntPtr.Zero || small[0] == IntPtr.Zero)
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            return false;
        }

        _large = large[0];
        _small = small[0];
        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
