using System.Windows.Media;

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
/// </summary>
internal static class BrushCache
{
    /// <summary>帯の色を敷くときの濃さ。モックの rgba(...,.1) 相当。</summary>
    internal const byte FaceAlpha = 28;

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
