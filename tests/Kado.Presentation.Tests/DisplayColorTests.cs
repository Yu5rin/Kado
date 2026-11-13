using Kado.Presentation.Infrastructure;

namespace Kado.Presentation.Tests;

/// <summary>
/// カレンダーの色を、表示のときだけ配色の地に合わせて明るさを直す計算。
/// <para>
/// 暗い藍は夜間の面で沈み、明るい黄は白い面で見えない。地に対して 3:1 に満たない色だけ、
/// HSL の明るさを地から遠ざける向きに動かす。色相と彩度は保つ。満たす色は変えない。
/// </para>
/// </summary>
public class DisplayColorTests
{
    private static readonly Rgb Light = new(0xFF, 0xFF, 0xFF);
    private static readonly Rgb Dark = new(0x20, 0x24, 0x2C);
    private static readonly Rgb Night = new(0x3E, 0x43, 0x48);

    private static Rgb C(string hex) => Rgb.TryParse(hex, out var color) ? color : throw new FormatException(hex);

    [Fact]
    public void 黒と白のコントラスト比は21対1()
    {
        Assert.Equal(21.0, DisplayColor.ContrastRatio(C("#000000"), C("#FFFFFF")), 6);
        Assert.Equal(1.0, DisplayColor.ContrastRatio(C("#808080"), C("#808080")), 6);
    }

    [Fact]
    public void 夜間の面に沈む藍は3対1以上に明るくなる()
    {
        var indigo = C("#27528F");
        Assert.True(DisplayColor.ContrastRatio(indigo, Night) < 3.0);

        var fixedColor = DisplayColor.EnsureContrast(indigo, Night);

        Assert.True(DisplayColor.ContrastRatio(fixedColor, Night) >= 3.0);
        Assert.True(DisplayColor.RelativeLuminance(fixedColor) > DisplayColor.RelativeLuminance(indigo));
    }

    [Fact]
    public void ライトで明るすぎる黄は暗くなる()
    {
        var yellow = C("#FBD75B");
        Assert.True(DisplayColor.ContrastRatio(yellow, Light) < 3.0);

        var fixedColor = DisplayColor.EnsureContrast(yellow, Light);

        Assert.True(DisplayColor.ContrastRatio(fixedColor, Light) >= 3.0);
        Assert.True(DisplayColor.RelativeLuminance(fixedColor) < DisplayColor.RelativeLuminance(yellow));
    }

    [Theory]
    [InlineData("#2F7D5B")]
    [InlineData("#A8621C")]
    [InlineData("#27528F")]
    public void もともと満たす色は変わらない(string hex)
    {
        var color = C(hex);
        Assert.True(DisplayColor.ContrastRatio(color, Light) >= 3.0);

        Assert.Equal(color, DisplayColor.EnsureContrast(color, Light));
    }

    [Fact]
    public void 色相と彩度は保たれる()
    {
        var indigo = C("#27528F");
        var fixedColor = DisplayColor.EnsureContrast(indigo, Night);

        var (h0, s0, _) = Hsl(indigo);
        var (h1, s1, _) = Hsl(fixedColor);

        // 8bit に丸めるぶんの揺れだけ許す
        Assert.InRange(Math.Abs(h0 - h1), 0, 0.02);
        Assert.InRange(Math.Abs(s0 - s1), 0, 0.08);
    }

    [Fact]
    public void 遠ざかる向きで届かない色は反対の向きで探す()
    {
        // 暗い地の上の真っ黒は、暗くしても届かない。明るくして 3:1 を満たす
        var fixedColor = DisplayColor.EnsureContrast(C("#000000"), Dark);

        Assert.True(DisplayColor.ContrastRatio(fixedColor, Dark) >= 3.0);
    }

    [Theory]
    [InlineData("#FFFFFF")]
    [InlineData("#20242C")]
    [InlineData("#3E4348")]
    public void どんな色でも_どの地の上でも_3対1以上になる(string backgroundHex)
    {
        var background = C(backgroundHex);

        for (var r = 0; r <= 255; r += 51)
        for (var g = 0; g <= 255; g += 51)
        for (var b = 0; b <= 255; b += 51)
        {
            var fixedColor = DisplayColor.EnsureContrast(new Rgb((byte)r, (byte)g, (byte)b), background);

            Assert.True(
                DisplayColor.ContrastRatio(fixedColor, background) >= 3.0,
                $"{new Rgb((byte)r, (byte)g, (byte)b)} on {backgroundHex} -> {fixedColor}");
        }
    }

    [Theory]
    [InlineData("#27528f", 0x27, 0x52, 0x8F)]
    [InlineData("27528F", 0x27, 0x52, 0x8F)]
    public void 色の文字列を読める(string text, byte r, byte g, byte b)
    {
        Assert.True(Rgb.TryParse(text, out var color));
        Assert.Equal(new Rgb(r, g, b), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void 壊れた色の文字列は読めない(string? text) => Assert.False(Rgb.TryParse(text, out _));

    private static (double H, double S, double L) Hsl(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;
        if (d == 0) return (0, 0, l);

        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }
}
