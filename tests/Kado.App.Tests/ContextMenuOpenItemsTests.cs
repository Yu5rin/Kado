using System.Text.RegularExpressions;

namespace Kado.App.Tests;

/// <summary>
/// 予定・タスク・日付の右クリックメニューを、アプリを起動せずに XAML から検査する（WPF は Linux では動かせない）。
/// <para>
/// メニューは <b>Themes/Controls.xaml の共有の4つ</b>（予定・タスク・右ペインのタスクの行・日付）に1つずつ書き、
/// 画面は参照するだけにしてある。以前は画面ごとに同じ並びのメニューを書いていて、1つ足すたびに足し忘れる画面が出た。
/// ここでは、①共有メニューの並び（足し忘れ・並びの食い違い）、②全部の画面が共有メニューを使っていること、
/// ③メニューを付ける要素が <c>Tag</c> に本体を持つこと、④項目が結ぶコマンドが本体にあること、
/// ⑤スタイルの引き継ぎ（<c>BasedOn</c>）と前方参照（起動時に落ちる）を見る。
/// </para>
/// </summary>
public class ContextMenuOpenItemsTests
{
    private const string Event = "EventContextMenu";
    private const string Task = "TaskContextMenu";
    private const string TaskRow = "TaskRowContextMenu";
    private const string Day = "DayContextMenu";

    private static readonly string[] SharedMenus = [Event, Task, TaskRow, Day];

    // ------------------------------------------------------------------
    // 共有メニューの並び
    // ------------------------------------------------------------------

    [Fact]
    public void 予定のメニューの並びは決まっている()
    {
        Assert.Equal(
            [
                "EventEditMenuItemStyle",
                "EventLinkMenuItemStyle",
                "OpenAttachmentMenuItemStyle",
                "-",
                "EventDuplicateMenuItemStyle",
                "EventMoveMenuItemStyle",
                "EventCopyMenuItemStyle",
                "-",
                "EventDeleteMenuItemStyle",
            ],
            Items(Event));
    }

    [Fact]
    public void タスクのメニューの並びは決まっている()
    {
        Assert.Equal(
            [
                "TaskEditMenuItemStyle",
                "TaskDoneMenuItemStyle",
                "TaskDueMenuItemStyle",
                "TaskListMoveMenuItemStyle",
                "TaskLinkMenuItemStyle",
                "OpenAttachmentMenuItemStyle",
                "-",
                "TaskDuplicateMenuItemStyle",
                "TaskCopyMenuItemStyle",
                "-",
                "TaskDeleteMenuItemStyle",
            ],
            Items(Task));
    }

    [Fact]
    public void 右ペインのタスクの行のメニューは_タスクのメニューに上へと下へを足しただけ()
    {
        var row = Items(TaskRow).ToList();
        var plain = Items(Task).ToList();

        // 削除の上に「上へ移動」「下へ移動」を、区切り線で分けて置く
        var at = row.IndexOf("TaskMoveUpMenuItemStyle");
        Assert.True(at > 0, "「上へ移動」が無い");
        Assert.Equal(["TaskMoveUpMenuItemStyle", "TaskMoveDownMenuItemStyle", "-"], row.GetRange(at, 3));
        Assert.Equal("TaskDeleteMenuItemStyle", row[^1]);
        Assert.Equal("-", row[at - 1]);

        row.RemoveRange(at, 3);
        Assert.Equal(plain, row);
    }

    [Fact]
    public void 日付のメニューの並びは決まっている()
    {
        Assert.Equal(
            [
                "DayAddEventMenuItemStyle",
                "DayAddTaskMenuItemStyle",
                "-",
                "DayShowDayMenuItemStyle",
                "DayShowWeekMenuItemStyle",
                "DayShowMonthMenuItemStyle",
            ],
            Items(Day));
    }

    [Fact]
    public void 予定のメニューにタスク用の項目を入れていない()
    {
        // 予定にタスクのコマンドを結ぶと、予定のリンクがタスクの開き方で開かれてしまう
        Assert.DoesNotContain(Items(Event), key => key.StartsWith("Task", StringComparison.Ordinal));

        foreach (var menu in new[] { Task, TaskRow })
        {
            Assert.DoesNotContain(Items(menu), key => key.StartsWith("Event", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(Items(Day), key => key.StartsWith("Event", StringComparison.Ordinal) ||
                                                 key.StartsWith("Task", StringComparison.Ordinal));
    }

    [Fact]
    public void 項目のスタイルはすべて定義されている()
    {
        var controls = Controls();

        foreach (var menu in SharedMenus)
        {
            foreach (var key in Items(menu).Where(k => k != "-"))
            {
                Assert.Matches("<Style x:Key=\"" + key + "\" TargetType=\"MenuItem\"", controls);
            }
        }
    }

    // ------------------------------------------------------------------
    // 画面は共有メニューを使う
    // ------------------------------------------------------------------

    /// <summary>画面ごとに使ってよい共有メニュー。足し忘れ（ここにあるのに参照していない）も、勝手な追加も止める。</summary>
    private static readonly Dictionary<string, string[]> ExpectedUse = new(StringComparer.Ordinal)
    {
        // 月：予定・タスクのチップ、日付の行のラベル、マスの空いた所
        ["Views/MonthView.xaml"] = [Event, Task, Day],

        // 週：終日レーンの予定とタスク、日付の見出し、日付の行のラベル
        ["Views/WeekView.xaml"] = [Event, Task, Day],

        // 日：終日レーンの予定とタスク、日付の行のラベル
        ["Views/DayView.xaml"] = [Event, Task],

        // 週・日の時間軸：予定のブロック、時間帯の空いた所
        ["Views/TimelineColumnView.xaml"] = [Event, Day],

        // 一覧：予定、タスク、日付の見出し
        ["Views/AgendaView.xaml"] = [Event, Task, Day],

        // 右ペイン：予定の行、タスクの行（並べ替えつき）、日付の行のラベル、空いた所
        ["Views/DayPaneView.xaml"] = [Event, TaskRow, Day],

        // 年：日付
        ["Views/YearView.xaml"] = [Day],
    };

    [Fact]
    public void すべての画面が共有メニューを使っている()
    {
        foreach (var (file, expected) in ExpectedUse)
        {
            var used = UsedMenus(File.ReadAllText(Path.Combine(AppDirectory, file)))
                .Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();

            Assert.True(
                expected.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(used),
                $"{file} が使うメニューが違う。期待 [{string.Join(", ", expected)}]、実際 [{string.Join(", ", used)}]");
        }
    }

    [Fact]
    public void 画面ごとに予定やタスクのメニューを書いていない()
    {
        // 画面（Views）は ContextMenu を定義せず、共有のものを参照するだけ。MainWindow.xaml の
        // 左パネルの行・ボタンのメニュー（カレンダー一覧用）は別
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppDirectory, "Views"), "*.xaml"))
        {
            var text = WithoutComments(File.ReadAllText(file));

            Assert.DoesNotMatch(@"<ContextMenu\b", text);
            Assert.DoesNotContain("PlacementTarget.Tag.EditChipCommand", text, StringComparison.Ordinal);

            // 共有メニュー以外を結んでいない
            foreach (var key in UsedMenus(text))
            {
                Assert.Contains(key, SharedMenus);
            }
        }
    }

    [Fact]
    public void 古い画面ごとのメニューの名前が残っていない()
    {
        string[] old =
        [
            "EventChipMenu", "TaskChipMenu", "AllDayEventMenu", "AllDayTaskMenu", "BlockMenu", "AgendaEventMenu",
            "AgendaTaskMenu", "EventRowMenu", "TaskRowMenu", "YearDayMenu", "DayHeaderMenu", "MilestoneMenu",
        ];

        foreach (var file in XamlFiles())
        {
            var text = WithoutComments(File.ReadAllText(file));

            foreach (var name in old)
            {
                Assert.DoesNotContain($"\"{name}\"", text, StringComparison.Ordinal);
                Assert.DoesNotContain($"StaticResource {name}}}", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void メニューを付ける要素はTagに本体を持っている()
    {
        // ContextMenu は画面の木から外れて出るので、項目は PlacementTarget.Tag から本体（MainViewModel）を引く。
        // Tag が無いと、項目は何も起こさない（押しても反応しない）
        var checkedCount = 0;

        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppDirectory, "Views"), "*.xaml"))
        {
            var text = WithoutComments(File.ReadAllText(file));

            foreach (Match element in Regex.Matches(
                         text, "<(\\w+)\\s[^<>]*?ContextMenu=\"\\{StaticResource (\\w+)\\}\"[^<>]*>", RegexOptions.Singleline))
            {
                checkedCount++;

                Assert.True(
                    Regex.IsMatch(element.Value, "\\bTag=\"\\{Binding DataContext,\\s*RelativeSource=\\{RelativeSource AncestorType="),
                    $"{Path.GetFileName(file)} の {element.Groups[1].Value}（{element.Groups[2].Value}）に Tag が無い");
            }
        }

        // 月 3+1（チップ・帯・タスク・ラベル・マス2）、週 4、日 3、時間軸 2、一覧 3、右ペイン 4、年 2。拾えていなければ検査が空振り
        Assert.True(checkedCount >= 20, $"メニューを付けた要素を {checkedCount} 個しか拾えていない");
    }

    [Fact]
    public void 時間帯の空いた所は右ボタンを押した時点で時刻を控える()
    {
        // 「この日に予定を追加」は、右クリックした時刻（15分に丸めた）から1時間の予定にする。
        // メニューは右ボタンを離したときに開くので、押した時点で時刻を控える
        var xaml = File.ReadAllText(Path.Combine(AppDirectory, "Views", "TimelineColumnView.xaml"));
        var code = File.ReadAllText(Path.Combine(AppDirectory, "Views", "TimelineColumnView.xaml.cs"));

        Assert.Matches("<Border x:Name=\"Column\"[^>]*PreviewMouseRightButtonDown=\"OnColumnRightPressed\"", xaml);
        Assert.Contains("DayMenu.SetTime(Column, TimeAt(", code, StringComparison.Ordinal);

        // 日付のメニューは、その時刻を読む
        Assert.Contains("PlacementTarget.(views:DayMenu.Time)", Controls(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目が結ぶコマンド
    // ------------------------------------------------------------------

    [Fact]
    public void リンクの項目は予定とタスクでそれぞれのコマンドに結んである()
    {
        Assert.Contains("Tag.OpenEventLinkCommand", StyleOf("EventLinkMenuItemStyle"), StringComparison.Ordinal);
        Assert.Contains("Tag.OpenTaskLinkCommand", StyleOf("TaskLinkMenuItemStyle"), StringComparison.Ordinal);
    }

    [Fact]
    public void メニューが結ぶコマンドは本体の画面モデルにある()
    {
        var viewModel = MainViewModelSource();
        var controls = WithoutComments(Controls());

        var commands = Regex.Matches(controls, @"PlacementTarget\.Tag\.(\w+Command)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();

        // 共有メニューの項目が結ぶコマンド。拾えていなければ検査が空振り
        Assert.True(commands.Length >= 18, $"コマンドを {commands.Length} 個しか拾えていない");

        // 添付の子メニューの項目が起こすコマンドも、本体にある
        foreach (var name in commands.Concat(["OpenEventAttachmentCommand", "OpenTaskAttachmentCommand"]))
        {
            Assert.Matches($@"public \S+ {name} \{{ get;( private set;)? \}}", viewModel);
        }
    }

    [Fact]
    public void 子メニューの項目は押したときのコマンドを自分で持っている()
    {
        // 子メニューは別のポップアップに出て ContextMenu をたどれない。
        // コマンドも、押せるかも、理由も、項目（MenuChoice）から読む
        var style = StyleOf("MenuChoiceItemStyle");

        Assert.Contains(Q("Property='Command' Value='{Binding Command}'"), style, StringComparison.Ordinal);
        Assert.Contains(Q("Property='CommandParameter' Value='{Binding Parameter}'"), style, StringComparison.Ordinal);
        Assert.Contains(Q("Property='IsEnabled' Value='{Binding IsEnabled}'"), style, StringComparison.Ordinal);
        Assert.Contains(Q("Property='ToolTip' Value='{Binding ToolTip}'"), style, StringComparison.Ordinal);

        // 灰色の項目にもツールチップを出す
        Assert.Contains("ToolTipService.ShowOnDisabled", style, StringComparison.Ordinal);

        foreach (var key in new[] { "EventMoveMenuItemStyle", "TaskDueMenuItemStyle", "TaskListMoveMenuItemStyle" })
        {
            Assert.Contains(
                Q("Property='ItemContainerStyle' Value='{StaticResource MenuChoiceItemStyle}'"),
                StyleOf(key), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 押せない項目には理由をツールチップに出す()
    {
        foreach (var (key, binding) in new[]
                 {
                     ("EventDuplicateMenuItemStyle", "DuplicateDisabledReason"),
                     ("EventMoveMenuItemStyle", "MoveDisabledReason"),
                     ("EventDeleteMenuItemStyle", "DeleteDisabledReason"),
                     ("TaskListMoveMenuItemStyle", "ListMoveDisabledReason"),
                 })
        {
            Assert.Contains(
                Q("Property='ToolTip' Value='{Binding " + binding + "}'"), StyleOf(key), StringComparison.Ordinal);
        }

        // 灰色でもツールチップが出る（項目の土台に置く）
        foreach (var key in new[] { "EventMenuItemStyle", "TaskMenuItemStyle" })
        {
            Assert.Contains("ToolTipService.ShowOnDisabled", StyleOf(key), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 予定の先頭の項目は編集できない予定で詳細を見るに切り替わる()
    {
        Assert.Contains(Q("Property='Header' Value='{Binding EditLabel}'"), StyleOf("EventEditMenuItemStyle"), StringComparison.Ordinal);
        Assert.Contains(Q("Property='Header' Value='{Binding DoneLabel}'"), StyleOf("TaskDoneMenuItemStyle"), StringComparison.Ordinal);
    }

    [Fact]
    public void 日付のメニューは今いるビューと同じものを出さない()
    {
        foreach (var (key, flag) in new[]
                 {
                     ("DayShowDayMenuItemStyle", "ShowsDay"),
                     ("DayShowWeekMenuItemStyle", "ShowsWeek"),
                     ("DayShowMonthMenuItemStyle", "ShowsMonth"),
                 })
        {
            Assert.Contains("{Binding " + flag + ", Converter=", StyleOf(key), StringComparison.Ordinal);
        }

        // ビューが変わったら出し分けを作り直させる（右ペインのように、ビューが変わっても残る場所のため）
        Assert.Contains("PlacementTarget.Tag.CurrentView", StyleOf("DayMenuItemStyle"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // スタイルの引き継ぎと前方参照
    // ------------------------------------------------------------------

    [Fact]
    public void MenuItemとSeparatorのスタイルは暗黙スタイルを引き継いでいる()
    {
        // Style を明示すると暗黙のスタイルは当たらず、枠や色が Windows 既定に戻る。BasedOn が要る。
        // 例外は暗黙スタイルそのもの（Controls.xaml の、x:Key も BasedOn も無い3つ）だけ
        var implicitStyles = 0;

        foreach (var file in XamlFiles())
        {
            var text = WithoutComments(File.ReadAllText(file));

            foreach (Match style in Regex.Matches(text, "<Style\\b[^<>]*\\bTargetType=\"(MenuItem|Separator|ContextMenu)\"[^<>]*>"))
            {
                if (Path.GetFileName(file) == "Controls.xaml" &&
                    style.Value == "<Style TargetType=\"" + style.Groups[1].Value + "\">")
                {
                    implicitStyles++;
                    continue;
                }

                Assert.True(
                    style.Value.Contains("BasedOn=", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} の {style.Value} に BasedOn が無い");
            }
        }

        Assert.Equal(3, implicitStyles);
    }

    [Fact]
    public void 完了にするの項目は暗黙スタイルを引き継ぐ()
    {
        // これまで「完了にする」の項目は、画面ごとに BasedOn の無い Style を持っていた
        Assert.Contains(
            Q("BasedOn='{StaticResource TaskMenuItemStyle}'"), StyleOpening("TaskDoneMenuItemStyle"), StringComparison.Ordinal);
        Assert.Contains(
            Q("BasedOn='{StaticResource {x:Type MenuItem}}'"), StyleOpening("TaskMenuItemStyle"), StringComparison.Ordinal);
    }

    [Fact]
    public void Controls_xamlのStaticResourceは定義より前に書かれていない()
    {
        // StaticResource は前方参照できない。ビルドは通るのに、起動時（辞書の読み込み）で落ちる。
        // 後ろで定義するものは DynamicResource で引くか、定義より後ろに置く
        var text = WithoutComments(Controls());

        var definitions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, "x:Key=\"([^\"]+)\""))
        {
            definitions.TryAdd(match.Groups[1].Value, match.Index);
        }

        var forward = Regex.Matches(text, @"\{StaticResource\s+([A-Za-z_]\w*)\}")
            .Where(m => definitions.TryGetValue(m.Groups[1].Value, out var at) && at > m.Index)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();

        Assert.Empty(forward);

        // 暗黙スタイルを BasedOn で引き継ぐものは、暗黙スタイルより後ろに置く
        var implicitMenuItem = text.IndexOf("<Style TargetType=\"MenuItem\">", StringComparison.Ordinal);
        var implicitSeparator = text.IndexOf("<Style TargetType=\"Separator\">", StringComparison.Ordinal);

        Assert.True(implicitMenuItem > 0 && implicitSeparator > 0);

        foreach (Match match in Regex.Matches(text, @"StaticResource \{x:Type (MenuItem|Separator)\}"))
        {
            var at = match.Groups[1].Value == "MenuItem" ? implicitMenuItem : implicitSeparator;

            Assert.True(at < match.Index, $"暗黙の {match.Groups[1].Value} スタイルより前で BasedOn している");
        }
    }

    [Fact]
    public void 共有メニューは項目のスタイルより後ろで定義している()
    {
        var text = WithoutComments(Controls());

        foreach (var menu in SharedMenus)
        {
            var at = text.IndexOf("<ContextMenu x:Key=\"" + menu + "\"", StringComparison.Ordinal);
            Assert.True(at > 0, $"{menu} が無い");

            foreach (var key in Items(menu).Where(k => k != "-"))
            {
                var style = text.IndexOf("<Style x:Key=\"" + key + "\"", StringComparison.Ordinal);
                Assert.True(style > 0 && style < at, $"{menu} より前に {key} を定義していない");
            }
        }
    }

    // ------------------------------------------------------------------

    /// <summary>読みやすさのため、XAML の断片は一重引用符で書いて、ここで二重引用符に直す。</summary>
    private static string Q(string xaml) => xaml.Replace('\'', '"');

    /// <summary>キーで指したスタイルの全文（&lt;Style …&gt; から &lt;/Style&gt; まで）。</summary>
    private static string StyleOf(string key)
    {
        var match = Regex.Match(
            WithoutComments(Controls()), "<Style x:Key=\"" + Regex.Escape(key) + "\".*?</Style>", RegexOptions.Singleline);

        Assert.True(match.Success, $"{key} が Themes/Controls.xaml に無い");

        return match.Value;
    }

    /// <summary>キーで指したスタイルの開始タグだけ（BasedOn を見る）。</summary>
    private static string StyleOpening(string key)
    {
        var match = Regex.Match(
            WithoutComments(Controls()), "<Style x:Key=\"" + Regex.Escape(key) + "\"[^>]*>", RegexOptions.Singleline);

        Assert.True(match.Success, $"{key} が Themes/Controls.xaml に無い");

        return match.Value;
    }

    /// <summary>メニューの項目を、上から並べる。項目は使っているスタイルのキー、区切り線は「-」。</summary>
    private static IReadOnlyList<string> Items(string menuKey)
    {
        var menu = Regex.Match(
            WithoutComments(Controls()),
            "<ContextMenu x:Key=\"" + menuKey + "\">(.*?)</ContextMenu>", RegexOptions.Singleline);

        Assert.True(menu.Success, $"{menuKey} が Themes/Controls.xaml に無い");

        var result = new List<string>();

        foreach (Match item in Regex.Matches(menu.Groups[1].Value, @"<(MenuItem|Separator)\b([^>]*)/>"))
        {
            if (item.Groups[1].Value == "Separator")
            {
                result.Add("-");
                continue;
            }

            var style = Regex.Match(item.Groups[2].Value, "Style=\"\\{StaticResource (\\w+)\\}\"");

            Assert.True(style.Success, $"{menuKey} に Style を持たない項目がある（{item.Value}）。項目はスタイルで書く");

            result.Add(style.Groups[1].Value);
        }

        return result;
    }

    /// <summary>画面が <c>ContextMenu="{StaticResource …}"</c> で使っているメニュー。</summary>
    private static IEnumerable<string> UsedMenus(string xaml) =>
        Regex.Matches(WithoutComments(xaml), "ContextMenu=\"\\{StaticResource (\\w+)\\}\"")
            .Select(m => m.Groups[1].Value);

    private static string Controls() => File.ReadAllText(Path.Combine(AppDirectory, "Themes", "Controls.xaml"));

    private static string MainViewModelSource()
    {
        var directory = Path.Combine(AppDirectory, "..", "Kado.Presentation", "ViewModels");

        return string.Concat(Directory.EnumerateFiles(directory, "MainViewModel*.cs").Select(File.ReadAllText));
    }

    /// <summary>コメントは検査の対象にしない（コメントの中に書いた使い方の例を拾わない）。</summary>
    private static string WithoutComments(string xaml) => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(AppDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

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
