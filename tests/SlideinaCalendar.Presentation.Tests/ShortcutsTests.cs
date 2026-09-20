using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// ショートカットの一覧。
/// <para>
/// グローバルホットキーは、押せることを知らないと一生使われない。実装と手で
/// 揃えているので、取り違えていないかをここで押さえる。
/// </para>
/// </summary>
public class ShortcutsTests
{
    [Fact]
    public void まとまりごとに中身を持つ()
    {
        Assert.NotEmpty(Shortcuts.All);
        Assert.All(Shortcuts.All, g =>
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Title));
            Assert.NotEmpty(g.Items);
        });
    }

    [Fact]
    public void 押すものと起きることが両方書いてある()
    {
        Assert.All(Shortcuts.All.SelectMany(g => g.Items), s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Keys));
            Assert.False(string.IsNullOrWhiteSpace(s.What));
        });
    }

    [Fact]
    public void グローバルホットキーが載っている()
    {
        var keys = Shortcuts.All.SelectMany(g => g.Items).Select(s => s.Keys).ToArray();

        // GlobalHotKeys.Attach が登録しているもの。書いていないと誰も押さない
        Assert.Contains("Ctrl＋Alt＋C", keys);
        Assert.Contains("Ctrl＋Alt＋N", keys);
    }

    [Fact]
    public void 本体のキーが載っている()
    {
        var keys = Shortcuts.All.SelectMany(g => g.Items).Select(s => s.Keys).ToArray();

        // MainWindow.xaml の InputBindings と揃える
        Assert.Contains("Ctrl＋Z", keys);
        Assert.Contains("Ctrl＋Y", keys);
        Assert.Contains("F5", keys);
    }

    [Fact]
    public void 同じまとまりの中で二度書かない()
    {
        // まとまりをまたげば同じ名前が出てよい。「ホイール」はカレンダーの上と
        // 時刻の欄で別のことをする
        Assert.All(Shortcuts.All, g =>
        {
            var keys = g.Items.Select(s => s.Keys).ToArray();

            Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        });
    }
}
