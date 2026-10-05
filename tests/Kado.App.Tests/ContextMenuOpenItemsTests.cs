using System.Text.RegularExpressions;

namespace Kado.App.Tests;

/// <summary>
/// 予定・タスクの右クリックメニューに「リンクを開く」「添付を開く」の項目があるかを、
/// アプリを起動せずに XAML から検査する（WPF は Linux では動かせない）。
/// <para>
/// 予定やタスクを右クリックできる所は画面ごとに別のメニューを持っている。1つ足し忘れると、
/// その画面でだけ開けない。「編集」の項目を持つメニューを全部拾って、足してあるかを見る。
/// </para>
/// </summary>
public class ContextMenuOpenItemsTests
{
    private sealed record Menu(string File, string Key, string Body);

    /// <summary>予定を右クリックしたときのメニュー（予定を編集する項目を持つもの）。</summary>
    private static readonly Regex EventMenu =
        new(@"Tag\.Edit(Chip|Event|Block|Milestone)Command", RegexOptions.Compiled);

    /// <summary>タスクを右クリックしたときのメニュー。右ペインの行は「編集」を持たず、並べ替えを持つ。</summary>
    private static readonly Regex TaskMenu =
        new(@"Tag\.(EditTaskChip|EditTask|MoveTaskUp)Command", RegexOptions.Compiled);

    [Fact]
    public void 予定を右クリックできるメニューにはリンクと添付の項目がある()
    {
        var menus = Menus().Where(m => EventMenu.IsMatch(m.Body)).ToArray();

        // 月・一覧・週の終日・日の終日・時間軸・右ペイン・日付の行のラベル（7つ）。減っていたら拾えていない
        Assert.True(menus.Length >= 7, $"予定のメニューを {menus.Length} 個しか拾えていない");

        foreach (var menu in menus)
        {
            Assert.True(
                Regex.IsMatch(menu.Body, @"\{(Static|Dynamic)Resource EventLinkMenuItemStyle\}"),
                $"{menu.File} の {menu.Key} に「リンクを開く」が無い");
            Assert.True(
                Regex.IsMatch(menu.Body, @"\{(Static|Dynamic)Resource OpenAttachmentMenuItemStyle\}"),
                $"{menu.File} の {menu.Key} に「添付を開く」が無い");
        }
    }

    [Fact]
    public void タスクを右クリックできるメニューにはリンクと添付の項目がある()
    {
        var menus = Menus().Where(m => TaskMenu.IsMatch(m.Body)).ToArray();

        // 月・一覧・週と日の終日レーン・右ペイン
        Assert.True(menus.Length >= 5, $"タスクのメニューを {menus.Length} 個しか拾えていない");

        foreach (var menu in menus)
        {
            Assert.True(
                Regex.IsMatch(menu.Body, @"\{(Static|Dynamic)Resource TaskLinkMenuItemStyle\}"),
                $"{menu.File} の {menu.Key} に「リンクを開く」が無い");
            Assert.True(
                Regex.IsMatch(menu.Body, @"\{(Static|Dynamic)Resource OpenAttachmentMenuItemStyle\}"),
                $"{menu.File} の {menu.Key} に「添付を開く」が無い");
        }
    }

    [Fact]
    public void 予定のメニューにタスク用の項目を入れていない()
    {
        // 予定にタスクのコマンドを結ぶと、予定のリンクがタスクの開き方で開かれてしまう
        foreach (var menu in Menus().Where(m => EventMenu.IsMatch(m.Body)))
        {
            Assert.DoesNotContain("TaskLinkMenuItemStyle", menu.Body, StringComparison.Ordinal);
        }

        foreach (var menu in Menus().Where(m => TaskMenu.IsMatch(m.Body)))
        {
            Assert.DoesNotContain("EventLinkMenuItemStyle", menu.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void リンクの項目は予定とタスクでそれぞれのコマンドに結んである()
    {
        var controls = File.ReadAllText(Path.Combine(AppDirectory, "Themes", "Controls.xaml"));

        Assert.Matches(StyleWithCommand("EventLinkMenuItemStyle", "OpenEventLinkCommand"), controls);
        Assert.Matches(StyleWithCommand("TaskLinkMenuItemStyle", "OpenTaskLinkCommand"), controls);
    }

    [Fact]
    public void メニューが結ぶコマンドは本体の画面モデルにある()
    {
        var viewModel = File.ReadAllText(
            Path.Combine(AppDirectory, "..", "Kado.Presentation", "ViewModels", "MainViewModel.cs"));

        foreach (var name in new[]
                 {
                     "OpenEventLinkCommand", "OpenEventAttachmentCommand",
                     "OpenTaskLinkCommand", "OpenTaskAttachmentCommand",
                 })
        {
            Assert.Matches($@"public \S+ {name} \{{ get; \}}", viewModel);
        }
    }

    private static Regex StyleWithCommand(string styleKey, string command) =>
        new($@"<Style x:Key=""{styleKey}""[^>]*>.*?Tag\.{command}", RegexOptions.Singleline);

    // ------------------------------------------------------------------

    private static IEnumerable<Menu> Menus()
    {
        foreach (var file in XamlFiles())
        {
            var text = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(text, @"<ContextMenu x:Key=""([^""]+)"">(.*?)</ContextMenu>", RegexOptions.Singleline))
            {
                yield return new Menu(Relative(file), match.Groups[1].Value, match.Groups[2].Value);
            }
        }
    }

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
