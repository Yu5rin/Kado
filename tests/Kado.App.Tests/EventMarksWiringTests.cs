using System.Text.RegularExpressions;

namespace Kado.App.Tests;

/// <summary>
/// 予定の題名の横の印（地球儀・クリップ）を、アプリを起動せずに XAML から検査する（足し忘れ防止）。
/// 出す画面は決まっている。1つ足すたびに足し忘れる画面が出ないよう、ここで全部を数える。
/// </summary>
public class EventMarksWiringTests
{
    // 印を出す画面と、そこに置く個数（テンプレートの数）
    private static readonly (string File, int Count)[] Placements =
    [
        ("MonthView.xaml", 2),          // チップ（EventChipTemplate）と複数日の帯（EventBandTemplate）
        ("WeekView.xaml", 1),           // 終日の予定
        ("DayView.xaml", 1),            // 終日の予定
        ("TimelineColumnView.xaml", 1), // 時間軸のブロック
        ("AgendaView.xaml", 1),         // 一覧の予定の行
        ("DayPaneView.xaml", 1),        // 右パネルの予定の行
    ];

    [Fact]
    public void 印は決めた画面に決めた数だけ置いてある()
    {
        foreach (var (file, count) in Placements)
        {
            Assert.Equal(count, Regex.Matches(Read("Views", file), "<views:EventMarks\\b").Count);
        }
    }

    [Fact]
    public void 印は題名の右に固定し_題名を残りに入れる()
    {
        // 題名が省略記号で縮んでも印が消えない作り（DockPanel の右に固定）
        foreach (var (file, _) in Placements)
        {
            foreach (Match mark in Regex.Matches(Read("Views", file), "<views:EventMarks\\b[^>]*>"))
            {
                Assert.Contains("DockPanel.Dock=\"Right\"", mark.Value, StringComparison.Ordinal);
                Assert.Contains("MarkSize=", mark.Value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void 狭い形の月のマスには印を置かない()
    {
        var month = Read("Views", "MonthView.xaml");
        var start = month.IndexOf("x:Key=\"CompactMarkTemplate\"", StringComparison.Ordinal);
        var end = month.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        var compactMarks = month[start..end];

        Assert.DoesNotContain("EventMarks", compactMarks, StringComparison.Ordinal);

        var cell = month.IndexOf("x:Key=\"MonthCellCompactTemplate\"", StringComparison.Ordinal);
        Assert.DoesNotContain("EventMarks", month[cell..], StringComparison.Ordinal);
    }

    [Fact]
    public void 印の部品はヒットテストを奪わず_ViewModelの値だけを読む()
    {
        var xaml = Read("Views", "EventMarks.xaml");

        Assert.Contains("IsHitTestVisible=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding HasLink", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding HasAttachments", xaml, StringComparison.Ordinal);
        Assert.Contains("{StaticResource IconLink}", xaml, StringComparison.Ordinal);
        Assert.Contains("{StaticResource IconAttachment}", xaml, StringComparison.Ordinal);

        // 色は配色のブラシから（直書きしない）
        Assert.DoesNotContain("#", xaml.Replace("#region", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void アイコン辞書に地球儀とクリップがある()
    {
        var icons = Read("Themes", "Icons.xaml");

        Assert.Matches("<PathGeometry\\s+x:Key=\"IconLink\"", icons);
        Assert.Matches("<PathGeometry\\s+x:Key=\"IconAttachment\"", icons);
    }

    private static string Read(string folder, string file) =>
        File.ReadAllText(Path.Combine(Locate(), folder, file));

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
