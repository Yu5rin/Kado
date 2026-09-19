using System.Windows;
using SlideinaCalendar.Presentation.Settings;

namespace SlideinaCalendar.App.Themes;

/// <summary>
/// 配色の切り替え。
/// <para>
/// 配色を入れた辞書を差し替えるだけで済むよう、色は Color リソースとして定義し、
/// ブラシは DynamicResource でそれを参照している。差し替えると画面全体が追随する。
/// </para>
/// </summary>
public static class ThemeManager
{
    /// <summary>Theme.xaml の中で、配色の辞書が何番目にあるか。</summary>
    private const int PaletteIndex = 0;

    /// <summary>現在の配色。</summary>
    public static ThemeChoice Current { get; private set; } = ThemeChoice.Auto;

    /// <summary>配色を切り替える。</summary>
    public static void Apply(ThemeChoice theme)
    {
        Current = theme;

        var resolved = theme == ThemeChoice.Auto ? DetectSystemTheme() : theme;
        var source = new Uri(
            resolved == ThemeChoice.Dark
                ? "pack://application:,,,/Themes/Dark.xaml"
                : "pack://application:,,,/Themes/Light.xaml",
            UriKind.Absolute);

        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged is null || merged.Count == 0) return;

        // Theme.xaml がひとつだけ読み込まれている前提。その中の配色を入れ替える
        var themeDictionary = merged[0];
        if (themeDictionary.MergedDictionaries.Count <= PaletteIndex) return;

        themeDictionary.MergedDictionaries[PaletteIndex] = new ResourceDictionary { Source = source };
    }

    /// <summary>
    /// OS の配色を読む。
    /// <para>
    /// レジストリの AppsUseLightTheme を見る。読めない場合はライト扱いにする。
    /// 判断できないときに暗くすると、文字が読めない画面が出かねない。
    /// </para>
    /// </summary>
    private static ThemeChoice DetectSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? ThemeChoice.Dark
                : ThemeChoice.Light;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return ThemeChoice.Light;
        }
    }
}
