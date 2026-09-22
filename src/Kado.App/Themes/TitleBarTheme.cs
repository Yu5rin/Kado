using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Kado.App.Themes;

/// <summary>
/// タイトルバーの配色。
/// <para>
/// タイトルバーは OS が描くので、中身をダークにしても<b>そこだけ白いまま残る</b>。
/// Windows の設定がライトで、こちらの配色だけをダークにしたときに目立つ。
/// </para>
/// <para>
/// DWM に頼んで暗くし、あわせて配色に合わせた色を渡す。色を指定できるのは
/// Windows 11 から。それより前では暗くするところまでしか効かないが、
/// 白いまま残るよりはよい。<b>効かない環境では黙って何もしない。</b>
/// </para>
/// </summary>
public static class TitleBarTheme
{
    /// <summary>暗くするか。Windows 10 の 2004 以降。</summary>
    private const int UseImmersiveDarkMode = 20;

    /// <summary>2004 より前の番号。20 が通らなかったときに試す。</summary>
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>タイトルバーの色。Windows 11 から。</summary>
    private const int CaptionColor = 35;

    /// <summary>タイトルの文字色。Windows 11 から。</summary>
    private const int TextColor = 36;

    /// <summary>窓の縁の色。Windows 11 から。</summary>
    private const int BorderColor = 34;

    /// <summary>
    /// いま出ているすべての窓に当てる。配色を切り替えたあとに呼ぶ。
    /// </summary>
    public static void ApplyToAll()
    {
        if (Application.Current is not { } app) return;

        foreach (Window window in app.Windows) Apply(window);
    }

    /// <summary>1つの窓に当てる。ハンドルができる前なら、できてから当て直す。</summary>
    public static void Apply(Window window)
    {
        if (window is null) return;

        var handle = new WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero)
        {
            // まだ窓ができていない。できた時点でもう一度
            window.SourceInitialized -= OnSourceInitialized;
            window.SourceInitialized += OnSourceInitialized;
            return;
        }

        Apply(handle, window);
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;

        window.SourceInitialized -= OnSourceInitialized;
        Apply(window);
    }

    private static void Apply(IntPtr handle, Window window)
    {
        var dark = IsDark(window);
        var flag = dark ? 1 : 0;

        // 2004 以降は 20、それより前は 19。どちらが通るかは版による
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref flag, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref flag, sizeof(int));
        }

        // 中身と同じ色にして、境目を消す。Windows 11 でだけ効く
        if (Brush(window, "PanelColor") is { } caption)
        {
            var value = ToBgr(caption);
            DwmSetWindowAttribute(handle, CaptionColor, ref value, sizeof(int));
        }

        if (Brush(window, "InkColor") is { } text)
        {
            var value = ToBgr(text);
            DwmSetWindowAttribute(handle, TextColor, ref value, sizeof(int));
        }

        if (Brush(window, "LineColor") is { } border)
        {
            var value = ToBgr(border);
            DwmSetWindowAttribute(handle, BorderColor, ref value, sizeof(int));
        }
    }

    /// <summary>
    /// 暗い配色か。
    /// <para>
    /// 設定の選び方ではなく、いま当たっている色そのものを見る。「自動」のときに
    /// OS の設定をもう一度読む手間が要らず、取り違えも起きない。
    /// </para>
    /// </summary>
    private static bool IsDark(Window window)
    {
        if (Brush(window, "PanelColor") is not { } color) return false;

        // 目の感じ方に合わせた明るさ。緑を重く、青を軽く見る
        var luminance = ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255.0;

        return luminance < 0.5;
    }

    /// <summary>配色から色を引く。無ければ null。</summary>
    private static Color? Brush(Window window, string key) =>
        window.TryFindResource(key) as Color?
        ?? Application.Current?.TryFindResource(key) as Color?;

    /// <summary>DWM は BGR の並びで受け取る。RGB のまま渡すと赤と青が入れ替わる。</summary>
    private static int ToBgr(Color color) => color.R | (color.G << 8) | (color.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);
}
