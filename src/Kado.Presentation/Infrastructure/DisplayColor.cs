namespace Kado.Presentation.Infrastructure;

/// <summary>sRGB の色（0〜255）。WPF の <c>Color</c> に依存せず、Linux のテストでも扱えるようにしてある。</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary><c>#rrggbb</c>（先頭の # は省略可）を読む。読めなければ false。</summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        if (text is null) return false;

        var span = text.AsSpan().Trim();
        if (span.Length == 7 && span[0] == '#') span = span[1..];
        if (span.Length != 6) return false;

        if (!byte.TryParse(span[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(span[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(span[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return false;
        }

        color = new Rgb(r, g, b);
        return true;
    }

    /// <summary><c>#RRGGBB</c>。</summary>
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// カレンダーの色（予定の帯・左パネルの色見本）を、いまの配色の地に合わせて<b>表示のときだけ</b>直す。
/// <para>
/// カレンダーの色は Google が決めた固定の値で、配色（ライト・ダーク・夜間）を替えても変わらない。
/// 暗い藍（#27528F）は夜間の面（#3E4348）の上でほとんど見えず、明るい黄（#FBD75B）は白い面の上で
/// 見えない。そこで、地に対するコントラスト比が基準（既定 3:1。図形・帯の WCAG の基準）に
/// 満たない色だけ、<b>HSL の明るさだけ</b>を地から遠ざける向きに少しずつ動かして基準を満たす。
/// 色相と彩度は保つ。もともと満たす色は変えない。
/// </para>
/// <para>
/// <b>保存している色・Google に送る色・色を選ぶ画面の見本には使わない。</b>表示専用の関数。
/// </para>
/// </summary>
public static class DisplayColor
{
    /// <summary>帯・色見本に求めるコントラスト比（WCAG の非テキストの基準）。</summary>
    public const double MinimumContrast = 3.0;

    /// <summary>明るさを動かす1回の幅（HSL の L、0〜1）。</summary>
    private const double Step = 0.005;

    /// <summary>WCAG の相対輝度。</summary>
    public static double RelativeLuminance(Rgb color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>WCAG のコントラスト比（1〜21）。2色の順は問わない。</summary>
    public static double ContrastRatio(Rgb a, Rgb b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>
    /// 地に対して <paramref name="minimum"/> 以上のコントラストになるよう、明るさだけ直した色を返す。
    /// 満たしている色はそのまま返す。
    /// <para>
    /// まず地から遠ざかる向き（色が地より明るければ明るく、暗ければ暗く）に動かす。その向きでは
    /// 届かないとき（暗い地の上の、さらに暗い色など）だけ、反対の向きで探す。
    /// </para>
    /// </summary>
    public static Rgb EnsureContrast(Rgb color, Rgb background, double minimum = MinimumContrast)
    {
        if (ContrastRatio(color, background) >= minimum) return color;

        var (hue, saturation, lightness) = ToHsl(color);
        var lighter = RelativeLuminance(color) >= RelativeLuminance(background);

        if (TryShift(hue, saturation, lightness, lighter, background, minimum, out var result)) return result;
        if (TryShift(hue, saturation, lightness, !lighter, background, minimum, out result)) return result;

        // どちらの向きでも届かない（地が中間の明るさで、基準が高すぎるなど）。いちばん離れた端を返す
        var white = new Rgb(255, 255, 255);
        var black = new Rgb(0, 0, 0);
        return ContrastRatio(white, background) >= ContrastRatio(black, background) ? white : black;
    }

    private static bool TryShift(
        double hue, double saturation, double lightness, bool lighter, Rgb background, double minimum, out Rgb result)
    {
        var direction = lighter ? 1 : -1;

        for (var l = lightness + direction * Step; l is >= 0 and <= 1; l += direction * Step)
        {
            var candidate = FromHsl(hue, saturation, l);
            if (ContrastRatio(candidate, background) >= minimum)
            {
                result = candidate;
                return true;
            }
        }

        result = default;
        return false;
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(Rgb color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var delta = max - min;

        if (delta == 0) return (0, 0, l);

        var s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double h;
        if (max == r) h = (g - b) / delta + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / delta + 2;
        else h = (r - g) / delta + 4;

        return (h / 6, s, l);
    }

    private static Rgb FromHsl(double h, double s, double l)
    {
        if (s == 0)
        {
            var gray = ToByte(l);
            return new Rgb(gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        return new Rgb(
            ToByte(Hue(p, q, h + 1.0 / 3)),
            ToByte(Hue(p, q, h)),
            ToByte(Hue(p, q, h - 1.0 / 3)));
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
