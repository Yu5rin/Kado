using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kado.App.Tests;

/// <summary>
/// 配色（Light／Dark／Night）と、見た目の直し（デザインの見直し）が戻されていないことを、
/// アプリを起動せずに検査する。
/// <para>
/// WPF は Linux で動かせず、画面は見られない。だから、(1) 3つの配色が同じキーを持っていること、
/// (2) 文字・枠のコントラストが基準を満たすこと、(3) 直した書き方が実在することを XAML の文字で見張る。
/// コントラスト比は WCAG の式（相対輝度）。<c>Kado.Presentation.Infrastructure.DisplayColor</c> と同じ式を
/// ここにも持つ（この試験は App プロジェクトも Presentation も参照しない作りのため）。
/// </para>
/// </summary>
public class ThemeDesignTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] Themes = ["Light", "Dark", "Night"];

    // ------------------------------------------------------------------
    // 3つの配色でキーが揃っている
    // ------------------------------------------------------------------

    [Fact]
    public void 三つの配色は同じ色のキーを持っている()
    {
        var keys = Themes.ToDictionary(t => t, t => Palette(t).Keys.ToHashSet());

        foreach (var theme in Themes)
        {
            var others = Themes.Where(t => t != theme).SelectMany(t => keys[t]).ToHashSet();
            var missing = others.Except(keys[theme]).OrderBy(k => k, StringComparer.Ordinal).ToArray();

            Assert.True(missing.Length == 0, $"{theme}.xaml に無い色のキー: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void 枠の色のキーは三つの配色にあり_ブラシも作られている()
    {
        foreach (var theme in Themes) Assert.True(Palette(theme).ContainsKey("ControlBorderColor"), theme);

        Assert.Matches(
            @"x:Key=""ControlBorderBrush""\s+Color=""\{DynamicResource ControlBorderColor\}""",
            ReadApp("Themes/Brushes.xaml"));
    }

    [Fact]
    public void Colorのキーにはすべて対応するブラシがあるか_意図して色のままのもの以外は()
    {
        // Brush を持たない Color は、他の色から計算に使うだけのもの。増やしたときは気付けるよう、数を固定せず
        // 「Brush のキーが Color のキーに対応している」ことだけ見る
        var brushes = Regex.Matches(ReadApp("Themes/Brushes.xaml"), @"Color=""\{DynamicResource (\w+)\}""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        var colors = Palette("Light").Keys.ToHashSet();

        Assert.Empty(brushes.Except(colors));
    }

    // ------------------------------------------------------------------
    // コントラスト
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Night")]
    public void 入力欄とチェックボックスの枠は地に対して3対1以上(string theme)
    {
        var palette = Palette(theme);

        Assert.True(Ratio(palette["ControlBorderColor"], palette["PanelColor"]) >= 3.0,
            $"{theme}: ControlBorder {palette["ControlBorderColor"]} / Panel {palette["PanelColor"]} = " +
            $"{Ratio(palette["ControlBorderColor"], palette["PanelColor"]):0.00}");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Night")]
    public void 補助文字Ink3は面の上で4_5対1以上(string theme)
    {
        var palette = Palette(theme);

        // 白パネル・一段沈めた面・休業日の面。ホバーの面は Night だけ文字が載る前提で設計している
        foreach (var face in new[] { "PanelColor", "SunkenColor", "OffDayColor" })
        {
            var ratio = Ratio(palette["Ink3Color"], palette[face]);
            Assert.True(ratio >= 4.5, $"{theme}: Ink3 {palette["Ink3Color"]} / {face} {palette[face]} = {ratio:0.00}");
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Night")]
    public void 完了の文字色DoneはInk3と同じ値(string theme)
    {
        var palette = Palette(theme);

        Assert.Equal(palette["Ink3Color"], palette["DoneColor"]);
    }

    [Fact]
    public void 夜間の赤_青_琥珀_今の時刻の線_補助文字は地とホバーの面で4_5対1以上()
    {
        var night = Palette("Night");

        foreach (var key in new[] { "SundayColor", "SaturdayColor", "CategoryAmberColor", "NowLineColor", "Ink3Color" })
        foreach (var face in new[] { "PanelColor", "CellHoverColor" })
        {
            var ratio = Ratio(night[key], night[face]);
            Assert.True(ratio >= 4.5, $"Night: {key} {night[key]} / {face} {night[face]} = {ratio:0.00}");
        }
    }

    [Fact]
    public void ライトの緑と琥珀は白の上で4_5対1以上()
    {
        var light = Palette("Light");

        foreach (var key in new[] { "CategoryGreenColor", "CategoryAmberColor" })
        {
            var ratio = Ratio(light[key], light["PanelColor"]);
            Assert.True(ratio >= 4.5, $"Light: {key} {light[key]} = {ratio:0.00}");
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Night")]
    public void 完了のチェック印は完了の面の上で3対1以上(string theme)
    {
        // チェック印は図形なので 3:1。Dark は Done を明るくしたので、印を白から濃色に替えてある
        var palette = Palette(theme);
        var ratio = Ratio(palette["OnDoneColor"], palette["DoneColor"]);

        Assert.True(ratio >= 3.0, $"{theme}: OnDone {palette["OnDoneColor"]} / Done {palette["DoneColor"]} = {ratio:0.00}");
    }

    // ------------------------------------------------------------------
    // 直した書き方が戻されていない
    // ------------------------------------------------------------------

    [Fact]
    public void FontWeightにMediumを使わない()
    {
        var offenders = XamlFiles()
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"FontWeight(=""|"" Value="")Medium"))
            .Select(Relative)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void 影の定義は一か所にまとまっている()
    {
        var definitions = XamlFiles()
            .Where(f => File.ReadAllText(f).Contains("<DropShadowEffect", StringComparison.Ordinal))
            .Select(Relative)
            .ToArray();

        Assert.Equal(["Themes/Controls.xaml"], definitions);

        var controls = ReadApp("Themes/Controls.xaml");
        Assert.Contains("""x:Key="PopupShadowEffect" BlurRadius="16" ShadowDepth="2" Opacity="0.30" """.TrimEnd(), controls);
        Assert.Equal(1, Regex.Matches(controls, "<DropShadowEffect").Count);
    }

    [Fact]
    public void 無効の濃さは0_7_同期の丸の印は白固定ではない()
    {
        Assert.Matches(@"x:Key=""DisabledOpacity"">0\.7<", ReadApp("Themes/Controls.xaml"));
        Assert.DoesNotContain("Stroke=\"White\"", ReadApp("MainWindow.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void 前後の月や休業日の文字を_Opacityで薄くしていない()
    {
        // 月ビューの前後の月・年ビューの休業日は Foreground を Ink3 に固定する（Opacity で薄くすると 4.5:1 を割る）
        Assert.DoesNotContain("Property=\"Opacity\" Value=\"0.6\"", ReadApp("Views/MonthView.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("Property=\"Opacity\" Value=\"0.45\"", ReadApp("Views/YearView.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void 文字の段は一段上がっていて_行の高さをスタイルが持つ()
    {
        var typography = ReadApp("Themes/Typography.xaml");

        foreach (var (key, size, line) in new[]
                 {
                     ("MicroTextStyle", "11", "15"),
                     ("CaptionTextStyle", "12", "16"),
                     ("BodyTextStyle", "13", "18"),
                     ("SubheadingTextStyle", "16", "22"),
                 })
        {
            var style = Regex.Match(typography, $@"x:Key=""{key}"".*?</Style>", RegexOptions.Singleline).Value;
            Assert.Contains($"Property=\"FontSize\" Value=\"{size}\"", style, StringComparison.Ordinal);
            Assert.Contains($"Property=\"LineHeight\" Value=\"{line}\"", style, StringComparison.Ordinal);
        }

        Assert.Matches(@"x:Key=""HeadingTextStyle"".*?Property=""FontSize"" Value=""20""", typography.ReplaceLineEndings(" "));
        Assert.Contains("x:Key=\"SectionHeaderTextStyle\"", typography, StringComparison.Ordinal);
    }

    [Fact]
    public void 押せる小さいボタンは24四方以上()
    {
        var controls = ReadApp("Themes/Controls.xaml");

        foreach (var key in new[] { "SmallIconButtonStyle", "RowDeleteButtonStyle" })
        {
            var style = Regex.Match(controls, $@"x:Key=""{key}"".*?</Style>", RegexOptions.Singleline).Value;
            Assert.Contains("Property=\"Width\" Value=\"24\"", style, StringComparison.Ordinal);
            Assert.Contains("Property=\"Height\" Value=\"24\"", style, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 日付を選ぶ暦はテーマの色で_テンプレートの部品名を保っている()
    {
        var controls = ReadApp("Themes/Controls.xaml");

        Assert.Contains("CalendarStyle\" Value=\"{StaticResource FieldCalendarStyle}\"", controls, StringComparison.Ordinal);

        foreach (var part in new[]
                 {
                     "PART_Root", "PART_PreviousButton", "PART_HeaderButton", "PART_NextButton",
                     "PART_MonthView", "PART_YearView", "PART_DisabledVisual", "DayTitleTemplateResourceKey",
                 })
        {
            Assert.Contains(part, controls, StringComparison.Ordinal);
        }

        // WPF 既定の VisualState 名（Calendar の部品が GoToState で呼ぶ）
        foreach (var state in new[]
                 {
                     "CommonStates", "SelectionStates", "CalendarButtonFocusStates", "ActiveStates",
                     "DayStates", "BlackoutDayStates", "CalendarButtonFocused", "Inactive", "Today", "BlackoutDay",
                 })
        {
            Assert.Contains($"x:Name=\"{state}\"", controls, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 削除の二つのボタンと設定の元に戻せない節は危険のボタン_閉じるだけの窓は副ボタン()
    {
        Assert.Contains("DangerButtonStyle", ReadApp("Views/EventEditorWindow.xaml"), StringComparison.Ordinal);
        Assert.Contains("DangerButtonStyle", ReadApp("Views/TaskEditorWindow.xaml"), StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(ReadApp("Views/SettingsWindow.xaml"), "DangerButtonStyle").Count);

        foreach (var file in new[] { "Views/SettingsWindow.xaml", "Views/ShortcutsWindow.xaml" })
        {
            var close = Regex.Match(ReadApp(file), @"<Button[^>]*Content=""閉じる""[^>]*>", RegexOptions.Singleline).Value;
            Assert.Contains("GhostButtonStyle", close, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 編集画面は中身をスクロールでき_窓の高さを作業領域で頭打ちにする()
    {
        foreach (var name in new[] { "EventEditorWindow", "TaskEditorWindow" })
        {
            Assert.Contains("<ScrollViewer VerticalScrollBarVisibility=\"Auto\">", ReadApp($"Views/{name}.xaml"), StringComparison.Ordinal);
            Assert.Contains("MaxHeight = SystemParameters.WorkArea.Height;", ReadApp($"Views/{name}.xaml.cs"), StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------

    /// <summary>配色ファイルの Color の x:Key → #RRGGBB（大文字）。</summary>
    private static Dictionary<string, string> Palette(string theme)
    {
        var document = XDocument.Parse(ReadApp($"Themes/{theme}.xaml"));

        return document.Descendants()
            .Where(e => e.Name.LocalName == "Color" && e.Attribute(X + "Key") is not null)
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value.Trim().ToUpperInvariant(), StringComparer.Ordinal);
    }

    private static double Luminance(string hex)
    {
        static double Channel(string h, int i)
        {
            var c = Convert.ToInt32(h.Substring(1 + i * 2, 2), 16) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(hex, 0) + 0.7152 * Channel(hex, 1) + 0.0722 * Channel(hex, 2);
    }

    private static double Ratio(string a, string b)
    {
        var (hi, lo) = Luminance(a) >= Luminance(b) ? (Luminance(a), Luminance(b)) : (Luminance(b), Luminance(a));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static string ReadApp(string relative) =>
        File.ReadAllText(Path.Combine(AppDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(AppDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string path) =>
        Path.GetRelativePath(AppDirectory, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string AppDirectory { get; } = Locate();

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Kado.App");
            if (Directory.Exists(candidate)) return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/Kado.App が見つかりません。");
    }
}
