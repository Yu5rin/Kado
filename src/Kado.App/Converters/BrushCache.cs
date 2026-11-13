using System.Windows.Media;
using Kado.Presentation.Infrastructure;

namespace Kado.App.Converters;

/// <summary>
/// <c>#rrggbb</c> などの色の文字列から、<b>凍結した</b>ブラシを作って使い回す。
/// <para>
/// 以前は、チップ1つにつき <c>ColorConverter.ConvertFromString</c> で色を解いて、新しい
/// <c>SolidColorBrush</c> を作っていた（凍結もしていない）。色はカレンダー・予定ごとに決まっていて
/// 種類は多くないので、同じ文字列には同じブラシを返す。凍結したブラシは変更できないが、
/// これらのブラシは面や枠に当てるだけで、アニメーションも書き換えもしない。
/// </para>
/// <para>
/// 壊れた色（解けない文字列）は、<see cref="Solid"/>・<see cref="Face"/> とも null。
/// 例外は1回目だけ（結果を覚える）。
/// </para>
/// <para>
/// 地の色（<c>background</c>）を渡す版は、<b>表示専用</b>：その地に対するコントラストが 3:1 に満たない色を、
/// 明るさだけ直して（<see cref="DisplayColor"/>）から使う。<b>鍵に地の色を含める</b>ので、配色を替えても
/// 前の配色で直した色が残らない（保存する色・Google に送る色・色の選択画面の見本は、地を渡さない版を使う）。
/// </para>
/// </summary>
internal static class BrushCache
{
    /// <summary>
    /// 帯の色を敷くときの濃さ。モックの rgba(...,.1) 相当の 28（約11%）では、暗い配色で
    /// 予定の帯の面が地と見分けられなかったため、56（約22%）に上げた。
    /// </summary>
    internal const byte FaceAlpha = 56;

    private static readonly KeyedCache<Brush> SolidBrushes = new();
    private static readonly KeyedCache<Brush> FaceBrushes = new();

    /// <summary>その色そのままの、凍結したブラシ。解けなければ null。</summary>
    public static Brush? Solid(string hex) =>
        SolidBrushes.GetOrCreate(hex, static key =>
            Parse(key) is { } color ? Freeze(new SolidColorBrush(color)) : null);

    /// <summary>その色を薄く敷いた面の、凍結したブラシ。解けなければ null。</summary>
    public static Brush? Face(string hex) =>
        FaceBrushes.GetOrCreate(hex, static key =>
            Parse(key) is { } color
                ? Freeze(new SolidColorBrush(
                    Color.FromArgb(FaceAlpha, color.R, color.G, color.B)))
                : null);

    /// <summary>
    /// 地の色に合わせて明るさを直した、凍結したブラシ（表示専用）。解けなければ null。
    /// 鍵は「色の文字列＋地の色」なので、配色ごとに別のブラシになる。
    /// </summary>
    public static Brush? Solid(string hex, Color background) =>
        SolidBrushes.GetOrCreate(KeyFor(hex, background), _ =>
            Adjusted(hex, background) is { } color ? Freeze(new SolidColorBrush(color)) : null);

    /// <summary>地の色に合わせて明るさを直した色を薄く敷いた面の、凍結したブラシ（表示専用）。解けなければ null。</summary>
    public static Brush? Face(string hex, Color background) =>
        FaceBrushes.GetOrCreate(KeyFor(hex, background), _ =>
            Adjusted(hex, background) is { } color
                ? Freeze(new SolidColorBrush(Color.FromArgb(FaceAlpha, color.R, color.G, color.B)))
                : null);

    /// <summary>地に対して 3:1 を満たすよう明るさを直した色。解けなければ null。</summary>
    internal static Color? Adjusted(string hex, Color background)
    {
        if (Parse(hex) is not { } color) return null;

        var fixedColor = DisplayColor.EnsureContrast(
            new Rgb(color.R, color.G, color.B),
            new Rgb(background.R, background.G, background.B));

        return Color.FromRgb(fixedColor.R, fixedColor.G, fixedColor.B);
    }

    private static string KeyFor(string hex, Color background) =>
        string.Concat(hex, "|", background.R.ToString("X2"), background.G.ToString("X2"), background.B.ToString("X2"));

    /// <summary>壊れた色でも表示は続ける。解けなければ null を返すだけで済ませる。</summary>
    internal static Color? Parse(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
