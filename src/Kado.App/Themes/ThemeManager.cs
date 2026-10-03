using System.Windows;
using Kado.Presentation.Settings;

namespace Kado.App.Themes;

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

    /// <summary>配色を束ねている入口。App.xaml が読み込んでいるもの。</summary>
    private static readonly Uri ThemeSource =
        new("pack://application:,,,/Themes/Theme.xaml", UriKind.Absolute);

    /// <summary>現在の配色（設定で選んだもの。自動のままのこともある）。</summary>
    public static ThemeChoice Current { get; private set; } = ThemeChoice.Auto;

    /// <summary>
    /// 実際に当てている配色。自動なら、そのとき Windows から読んだ明暗。
    /// Windows の明暗が変わったかどうかを比べるのに使う。
    /// </summary>
    private static ThemeChoice _applied = ThemeChoice.Light;

    /// <summary>
    /// Windows の明暗が切り替わっていたら、自動のときだけ当て直す。
    /// <para>
    /// システムの設定変更の通知（<c>UserPreferenceChanged</c>）は、無関係な変更でも何度も来る。
    /// 当て直しは辞書ごとの作り直しで重いので、明暗が実際に変わったときだけ払う。
    /// 画面のスレッドで呼ぶこと。
    /// </para>
    /// </summary>
    /// <returns>当て直したら true。</returns>
    public static bool ReapplyIfSystemChanged()
    {
        if (!ThemeFollow.ShouldReapply(Current, _applied, DetectSystemTheme())) return false;

        Apply(Current);
        return true;
    }

    /// <summary>配色を切り替える。</summary>
    public static void Apply(ThemeChoice theme)
    {
        Current = theme;

        var resolved = theme == ThemeChoice.Auto ? DetectSystemTheme() : theme;
        _applied = resolved;
        var source = new Uri(
            resolved switch
            {
                ThemeChoice.Dark => "pack://application:,,,/Themes/Dark.xaml",
                ThemeChoice.Night => "pack://application:,,,/Themes/Night.xaml",
                _ => "pack://application:,,,/Themes/Light.xaml",
            },
            UriKind.Absolute);

        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged is null || merged.Count == 0) return;

        // Theme.xaml ごと作り直してから、その中の配色を入れ替える。
        //
        // 中の1枚だけを差し替えると、すでに作られているブラシが古い色を持ったまま
        // 残ることがある。色は Color リソースを DynamicResource で参照しているが、
        // 一度固められたブラシはそれ以上変わらない。丸ごと作り直せば確実に変わる。
        // 起動のときに1回読むのと同じ手間なので、切り替えのたびに払ってよい
        var rebuilt = new ResourceDictionary { Source = ThemeSource };
        if (rebuilt.MergedDictionaries.Count <= PaletteIndex) return;

        rebuilt.MergedDictionaries[PaletteIndex] = new ResourceDictionary { Source = source };
        merged[0] = rebuilt;

        // タイトルバーは OS が描くので、辞書を入れ替えても追随しない。別に頼む
        TitleBarTheme.ApplyToAll();

        // コンバーターが引いたブラシ（マイルストーンのラベル・期限の強調色・同期の丸印・
        // 色を持たないカレンダーの既定色）は DynamicResource ではないので、前の配色のまま残る。
        // 結び直す
        ThemeBindingRefresh.RefreshAll();
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
