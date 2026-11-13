using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kado.App.Tests;

/// <summary>
/// 操作性とメニューの直しの配線を、アプリを起動せずにソースと XAML から検査する（WPF は Linux では動かせない）。
/// <para>
/// 判断そのものは <c>Kado.Presentation.Tests</c>（<c>OperabilityTests</c>）で試験している。ここでは、
/// 起動時にだけ壊れる書き方（InputBinding・Handled での文字の握りつぶし・メニューの並び）と、
/// 画面が判断を通していることを見張る。
/// </para>
/// </summary>
public class OperabilityWiringTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string SrcDirectory { get; } = Locate();

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(SrcDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string MainXaml { get; } = Read("Kado.App/MainWindow.xaml");

    private static string MainCode { get; } = Read("Kado.App/MainWindow.xaml.cs");

    private static string App { get; } = Read("Kado.App/App.xaml.cs");

    private static string WithoutComments(string xaml) => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"{start} が見つかりません。");

        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"{end} が見つかりません。");

        return source[from..to];
    }

    // ------------------------------------------------------------------
    // キー
    // ------------------------------------------------------------------

    [Fact]
    public void 窓のInputBindingsには修飾キー無しの単独キーを置かない()
    {
        // 入力欄の中で打ったときにまで奪う。かといって PreviewKeyDown を Handled にすると、文字そのものが入らない
        var doc = XDocument.Parse(MainXaml);
        var bindings = doc.Descendants(Wpf + "KeyBinding").ToList();

        Assert.NotEmpty(bindings);

        foreach (var binding in bindings)
        {
            var key = (string)binding.Attribute("Key")!;
            var modifiers = (string?)binding.Attribute("Modifiers");

            if (modifiers is { Length: > 0 }) continue;

            // 修飾キー無しで許すのは F キーだけ
            Assert.Matches("^F[0-9]+$", key);
        }
    }

    [Fact]
    public void 単独キーは入力欄にフォーカスが無いときだけコードで受ける()
    {
        var keys = Slice(MainCode, "private void OnPreviewKeyDown", "private bool HandleDaySelectionKey");

        // 入力欄の判定と、操作部品の判定の両方を通す
        Assert.Contains("KeyFocusPolicy.IsTextInput(", keys, StringComparison.Ordinal);
        Assert.Contains("KeyFocusPolicy.TakesNavigationKeys(", keys, StringComparison.Ordinal);

        // 今日（T・Home）、ビュー切替（1〜5・テンキー）、前へ・次へ
        foreach (var key in new[]
                 {
                     "Key.T", "Key.Home", "Key.PageUp", "Key.PageDown",
                     "Key.D1 or Key.NumPad1", "Key.D2 or Key.NumPad2", "Key.D3 or Key.NumPad3",
                     "Key.D4 or Key.NumPad4", "Key.D5 or Key.NumPad5",
                 })
        {
            Assert.Contains(key, keys, StringComparison.Ordinal);
        }

        // 文字を握りつぶさない。入力欄のときは Handled にせず、そのまま返す
        Assert.Contains("if (inTextInput || Keyboard.Modifiers != ModifierKeys.None) return;", keys, StringComparison.Ordinal);
        Assert.DoesNotContain("IsBareShortcutKey", MainCode, StringComparison.Ordinal);

        // 矢印・Enter・Delete は、自分でそれを使う操作部品にフォーカスがあるときは奪わない
        Assert.Contains("if (!onOperable && HandleDaySelectionKey(e.Key))", keys, StringComparison.Ordinal);
    }

    [Fact]
    public void F1とCtrlカンマが窓のキーに結ばれている()
    {
        var xaml = WithoutComments(MainXaml);

        Assert.Matches(@"<KeyBinding Key=""F1""[^>]*ShowShortcutsCommand", xaml);
        Assert.Matches(@"<KeyBinding Key=""OemComma"" Modifiers=""Control""[^>]*OpenSettingsCommand", xaml);
        Assert.Matches(@"<KeyBinding Key=""N"" Modifiers=""Control""[^>]*AddEventCommand", xaml);
        Assert.Matches(@"<KeyBinding Key=""N"" Modifiers=""Control\+Shift""[^>]*AddTaskCommand", xaml);
    }

    /// <summary>窓の KeyBinding の表記を、ショートカット一覧の表記（「Ctrl＋Z」）にする。</summary>
    private static string DisplayOf(string key, string? modifiers)
    {
        var name = key switch
        {
            "OemComma" => ",",
            _ => key,
        };

        if (string.IsNullOrEmpty(modifiers)) return name;

        var parts = modifiers.Split('+').Select(m => m == "Control" ? "Ctrl" : m).Append(name);
        return string.Join("＋", parts);
    }

    [Fact]
    public void 窓のキーは全部ショートカットの一覧に載っている()
    {
        var shortcuts = Read("Kado.Presentation/ViewModels/ShortcutsViewModel.cs");
        var doc = XDocument.Parse(MainXaml);

        foreach (var binding in doc.Descendants(Wpf + "KeyBinding"))
        {
            var shown = DisplayOf((string)binding.Attribute("Key")!, (string?)binding.Attribute("Modifiers"));

            Assert.True(
                shortcuts.Contains($"new(\"{shown}\"", StringComparison.Ordinal)
                || shortcuts.Contains($"new(\"{shown} ", StringComparison.Ordinal)
                || shortcuts.Contains($" ／ {shown}\"", StringComparison.Ordinal),
                $"ショートカットの一覧に「{shown}」が載っていない");
        }
    }

    [Fact]
    public void コードで受けるキーも一覧に載っている()
    {
        var shortcuts = Read("Kado.Presentation/ViewModels/ShortcutsViewModel.cs");

        // OnPreviewKeyDown / HandleBareKey / HandleDaySelectionKey が受けるもの
        foreach (var shown in new[]
                 {
                     "T ／ Home", "PageUp PageDown", "1 〜 5", "テンキー", "Ctrl＋F", "Ctrl＋L ／ ／",
                     "← →", "↑ ↓", "Enter", "Delete", "Esc", "Ctrl＋Enter", "F1", "Ctrl＋,",
                 })
        {
            Assert.Contains(shown, shortcuts, StringComparison.Ordinal);
        }

        // 実際の動きに合わせた文言（PageUp/PageDown が前へ・次へ、← → は日を1日動かす）
        Assert.Contains("new(\"← →\", \"選んでいる日を1日動かす\")", shortcuts, StringComparison.Ordinal);
        Assert.Contains("new(\"PageUp PageDown\", \"前へ・次へ", shortcuts, StringComparison.Ordinal);
        Assert.DoesNotContain("new(\"← → ／ PageUp PageDown\"", shortcuts, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // モードレスの窓
    // ------------------------------------------------------------------

    [Fact]
    public void モードレスの窓は閉じるボタンもEscも自分で閉じる()
    {
        foreach (var name in new[] { "WorkdayCalculatorWindow", "ShortcutsWindow" })
        {
            var xaml = Read($"Kado.App/Views/{name}.xaml");
            var code = Read($"Kado.App/Views/{name}.xaml.cs");

            Assert.Matches(@"IsCancel=""True""[^>]*Click=""OnCloseClick""", xaml);
            Assert.Contains("private void OnCloseClick", code, StringComparison.Ordinal);
            Assert.Contains("Close();", code, StringComparison.Ordinal);
            Assert.Contains("ModelessWindow.CloseOnEscape(this);", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ショートカットの窓はモードレスで出す()
    {
        var presenter = Read("Kado.App/Editing/DialogEditorPresenter.cs");
        var shortcuts = Slice(presenter, "public void ShowShortcuts()", "public bool ConfirmDelete");

        Assert.Contains("window.Show();", shortcuts, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", shortcuts, StringComparison.Ordinal);
    }

    [Fact]
    public void モードレスの窓はほかに無い()
    {
        // Show() で出す窓を数える。増やしたら、閉じ方（IsCancel は効かない）をここで気づく
        var shown = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(SrcDirectory, "Kado.App"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;

            shown.AddRange(Regex.Matches(File.ReadAllText(file), @"\b(\w+)\.Show\(\)")
                .Select(m => $"{Path.GetFileName(file)}:{m.Groups[1].Value}"));
        }

        // 本体の窓・実働日計算・ショートカット・トレイの通知の窓・ShellController の窓
        Assert.All(shown, entry => Assert.Matches(@"^(App\.xaml\.cs:window|App\.xaml\.cs:controller|DialogEditorPresenter\.cs:window|ToastWindow\.xaml\.cs:toast|ShellController\.cs:_window)$", entry));
    }

    // ------------------------------------------------------------------
    // 編集画面の閉じ方
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("EventEditorWindow")]
    [InlineData("TaskEditorWindow")]
    [InlineData("CalendarEditorWindow")]
    public void 編集画面は閉じるときの確認とCtrlEnterの保存を付けている(string name)
    {
        var code = Read($"Kado.App/Views/{name}.xaml.cs");

        Assert.Contains("EditorWindowGuard.Attach(this,", code, StringComparison.Ordinal);
        Assert.Contains("editor.IsDirty", code, StringComparison.Ordinal);
    }

    [Fact]
    public void 閉じるときの確認は変えているときだけ_保存と削除では尋ねない()
    {
        var guard = Read("Kado.App/Views/EditorWindowGuard.cs");

        Assert.Contains("Key.Enter", guard, StringComparison.Ordinal);
        Assert.Contains("ModifierKeys.Control", guard, StringComparison.Ordinal);
        Assert.Contains("window.PreviewKeyDown", guard, StringComparison.Ordinal);
        Assert.Contains("window.DialogResult == true || isDeleted()", guard, StringComparison.Ordinal);
        Assert.Contains("if (!isDirty()) return;", guard, StringComparison.Ordinal);
        Assert.Contains("\"変更を破棄しますか？\"", guard, StringComparison.Ordinal);
        Assert.Contains("\"破棄する\", \"編集に戻る\"", guard, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 右パネル・一覧のタスクのチェック
    // ------------------------------------------------------------------

    [Fact]
    public void 右パネルのタスクは完了の切り替えをチェックだけが受ける()
    {
        var xaml = WithoutComments(Read("Kado.App/Views/DayPaneView.xaml"));
        var code = Read("Kado.App/Views/DayPaneView.xaml.cs");

        // 押せるチェック
        var check = Regex.Match(xaml, @"<CheckBox[^>]*TaskCheckBoxStyle[^>]*/>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(check);
        Assert.DoesNotContain("IsHitTestVisible=\"False\"", check, StringComparison.Ordinal);
        Assert.Contains("ToggleTaskDoneCommand", check, StringComparison.Ordinal);

        // 待ちのタイマーをやめた。行の1回押しは何もしない
        Assert.DoesNotContain("DispatcherTimer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleTaskDoneCommand", code, StringComparison.Ordinal);
        Assert.Contains("if (e.ClickCount < 2) return;", code, StringComparison.Ordinal);

        // ドラッグでの並べ替えはそのまま
        Assert.Contains("DragActivity.DoDragDrop", code, StringComparison.Ordinal);
    }

    [Fact]
    public void 一覧のタスクの丸は押せるチェック()
    {
        var xaml = WithoutComments(Read("Kado.App/Views/AgendaView.xaml"));
        var task = Slice(xaml, "<DataTemplate x:Key=\"AgendaTaskTemplate\">", "</DataTemplate>");

        Assert.Contains("<CheckBox", task, StringComparison.Ordinal);
        Assert.Contains("ToggleTaskDoneCommand", task, StringComparison.Ordinal);
        Assert.DoesNotContain("<Ellipse", task, StringComparison.Ordinal);
        Assert.DoesNotContain("IsHitTestVisible=\"False\"", task, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 同期のボタン
    // ------------------------------------------------------------------

    [Fact]
    public void 同期のボタンは未接続なら設定を開く命令に結ばれ_走っているあいだは中止になる()
    {
        var xaml = WithoutComments(MainXaml);

        // 既定は SyncButtonCommand。Sync.SyncNowCommand を直には結ばない
        Assert.Equal(2, Regex.Matches(xaml, @"<Setter Property=""Command"" Value=""\{Binding SyncButtonCommand\}"" />").Count);
        Assert.Equal(2, Regex.Matches(xaml, @"<Setter Property=""Command"" Value=""\{Binding Sync\.CancelSyncCommand\}"" />").Count);
        Assert.DoesNotMatch(@"<Setter Property=""Command"" Value=""\{Binding Sync\.SyncNowCommand\}"" />", xaml);
    }

    [Fact]
    public void 自動更新は打ちかけの文字を見て入れ替えを待つ()
    {
        var state = Slice(App, "private AutoUpdateState BuildAutoUpdateState", "private void LogAutoUpdateWait");

        Assert.Contains("HasDraftInput: main?.HasDraftInput == true", state, StringComparison.Ordinal);
    }

    [Fact]
    public void 同期の右クリックは警告が無ければ出さない()
    {
        var xaml = WithoutComments(MainXaml);

        Assert.Contains("ContextMenuOpening=\"OnSyncContextMenuOpening\"", xaml, StringComparison.Ordinal);
        Assert.Contains("警告を確認済みにする", xaml, StringComparison.Ordinal);
        Assert.Contains("HasUnreadWarnings: false", MainCode, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 知らせの帯
    // ------------------------------------------------------------------

    [Fact]
    public void 失敗の知らせは自動で消さず_カーソルが載っているあいだは成功も消さない()
    {
        Assert.Contains("MouseEnter=\"OnStatusPointerEntered\" MouseLeave=\"OnStatusPointerLeft\"", MainXaml, StringComparison.Ordinal);
        Assert.Contains("DismissStatusCommand", MainXaml, StringComparison.Ordinal);

        var clear = Slice(MainCode, "private void ClearStatusIfIdle()", "/// <summary>設定ボタン");
        Assert.Contains("vm.IsStatusFailure || _pointerOnStatus", clear, StringComparison.Ordinal);

        var changed = Slice(MainCode, "private void OnStatusMessageChanged", "/// <summary>知らせの帯の上にカーソルがあるか");
        Assert.Contains("_statusClear.Cancel();", changed, StringComparison.Ordinal);
    }

    [Fact]
    public void 初回の案内は試すを持たず_2行と閉じるだけ()
    {
        var xaml = MainXaml;

        Assert.DoesNotContain("TryOnboarding", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"試す\"", xaml, StringComparison.Ordinal);
        Assert.Contains("予定はマスをダブルクリックで追加できます", xaml, StringComparison.Ordinal);
        Assert.Contains("Google と同期するには ⚙ → 設定… → Google", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 設定の窓の下の行に操作の結果を出し_失敗は赤_成功は通常の色()
    {
        var xaml = WithoutComments(Read("Kado.App/Views/SettingsWindow.xaml"));

        Assert.Contains("Text=\"{Binding Notice}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding NoticeIsFailure}\"", xaml, StringComparison.Ordinal);

        var code = Read("Kado.App/Views/SettingsWindow.xaml.cs");
        Assert.Contains("settings.Detach()", code, StringComparison.Ordinal);
        Assert.Contains("settings.Category = category", code, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // メニュー
    // ------------------------------------------------------------------

    /// <summary>メニューの並び。区切り線は "-"、見出しだけの項目は "[見出し]"、子メニューは "見出し ▸"。</summary>
    private static List<string> Layout(XElement menu)
    {
        var list = new List<string>();

        foreach (var child in menu.Elements())
        {
            if (child.Name == Wpf + "Separator")
            {
                list.Add("-");
                continue;
            }

            var header = (string?)child.Attribute("Header") ?? "?";
            list.Add(child.Elements(Wpf + "MenuItem").Any() ? header + " ▸" : header);
        }

        return list;
    }

    private static XElement AppMenu() =>
        XDocument.Parse(MainXaml).Descendants(Wpf + "ContextMenu")
            .Single(e => (string?)e.Attribute(X + "Key") == "AppMenu");

    [Fact]
    public void ギアのメニューの並びは決まっている()
    {
        Assert.Equal(
            [
                "予定を追加…", "タスクを追加…", "検索", "-",
                "元に戻す", "やり直す", "-",
                "{Binding Sync.MenuLabel}", "実働日計算…", "Excel から実働日を取り込む…", "配信元から実働日を取り込む",
                "パネル ▸", "寄せる辺 ▸", "-",
                "ショートカット…", "設定…", "-",
                "Kado を終了",
            ],
            Layout(AppMenu()));
    }

    [Fact]
    public void 狭いときだけ出す項目は_検索とパネルと寄せる辺だけ()
    {
        // ⚙ と「…」は同じメニューを共有する。違いはこの3項目の出し分けだけ
        var onlyWhenNarrow = AppMenu().Elements()
            .Where(e => ((string?)e.Attribute("Visibility"))?.Contains("UsesOverflowMenu", StringComparison.Ordinal) == true)
            .Select(e => (string)e.Attribute("Header")!)
            .ToList();

        Assert.Equal(["検索", "パネル", "寄せる辺"], onlyWhenNarrow);
    }

    [Fact]
    public void ギアと畳みのボタンは同じメニューを使い_ツールチップはメニュー()
    {
        var xaml = WithoutComments(MainXaml);

        var users = Regex.Matches(xaml, @"<Button[^>]*ToolTip=""メニュー""[^>]*ContextMenu=""\{StaticResource AppMenu\}""", RegexOptions.Singleline);
        Assert.Equal(2, users.Count);

        // 同じ並びをもう1か所に書いていない（「今すぐ同期」の項目が AppMenu の外に無い）
        Assert.DoesNotContain("Sync.SyncNowCommand}\"\n                                      InputGestureText", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ギアのメニューの項目に窓が開くものは三点リーダーが付いている()
    {
        var headers = AppMenu().Descendants(Wpf + "MenuItem").Select(e => (string?)e.Attribute("Header")).ToList();

        foreach (var header in new[] { "予定を追加…", "タスクを追加…", "実働日計算…", "Excel から実働日を取り込む…", "ショートカット…", "設定…" })
        {
            Assert.Contains(header, headers);
        }

        // 「…」は括弧の前に置かない。括弧は使わない
        Assert.DoesNotContain(headers, h => h is not null && Regex.IsMatch(h, "…（|\\(JSON\\)|（JSON）|（Excel）"));
    }

    [Fact]
    public void パネルのメニューの並びは決まっている()
    {
        var doc = XDocument.Parse(MainXaml);
        var button = doc.Descendants(Wpf + "Button").Single(b => (string?)b.Attribute("ToolTip") == "パネルと寄せる辺");
        var menu = button.Element(Wpf + "Button.ContextMenu")!.Element(Wpf + "ContextMenu")!;

        Assert.Equal(
            [
                "表示するパネル", "左パネル（ミニ月暦・カレンダー一覧）", "中央（カレンダー）", "右パネル（予定・タスク）", "-",
                "スライド・固定で寄せる辺", "左端", "右端",
            ],
            Layout(menu));
    }

    [Fact]
    public void トレイのメニューの並びは決まっている()
    {
        var tray = Slice(App, "private System.Windows.Controls.ContextMenu BuildTrayMenu", "private void OnHotKey");

        var items = new List<string>();

        foreach (Match m in Regex.Matches(tray, @"(?:Add\(""([^""]+)""|(Separator\(\));)"))
        {
            items.Add(m.Groups[2].Success ? "-" : m.Groups[1].Value);
        }

        Assert.Equal(
            [
                "Kado を開く", "予定を追加…", "-",
                "ウィンドウ", "スライド", "固定", "-",
                "左端", "右端", "-",
                "今すぐ同期", "設定…", "-",
                "Kado を終了",
            ],
            items);

        // ショートカットは外した。出しかたと辺は開くたびに今の状態でチェックする
        Assert.DoesNotContain("ショートカット", tray.Replace("ショートカットはここには出さない", string.Empty), StringComparison.Ordinal);
        Assert.Contains("menu.Opened +=", tray, StringComparison.Ordinal);
        Assert.Contains("dock.IsChecked = main.Shell.Mode == ShellMode.Dock;", tray, StringComparison.Ordinal);
        Assert.Contains("left.IsChecked = main.Shell.IsAtLeft;", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void 左パネルの行のメニューの文言()
    {
        var doc = XDocument.Parse(MainXaml);

        string[] Headers(string key) => doc.Descendants(Wpf + "ContextMenu")
            .Single(e => (string?)e.Attribute(X + "Key") == key)
            .Elements(Wpf + "MenuItem").Select(e => (string)e.Attribute("Header")!).ToArray();

        Assert.Equal(["新しい予定の入れ先にする", "名前と色を変える…", "削除"], Headers("SourceRowMenu"));
        Assert.Equal(["新しいタスクの入れ先にする", "名前を変える…", "削除"], Headers("TaskListRowMenu"));
    }

    [Fact]
    public void 日付のメニューの文言()
    {
        var controls = Read("Kado.App/Themes/Controls.xaml");

        foreach (var (key, header) in new[]
                 {
                     ("DayAddEventMenuItemStyle", "予定を追加…"),
                     ("DayAddTaskMenuItemStyle", "タスクを追加…"),
                     ("DayShowDayMenuItemStyle", "1日で見る"),
                     ("DayShowWeekMenuItemStyle", "週で見る"),
                     ("DayShowMonthMenuItemStyle", "月で見る"),
                     ("TaskEditMenuItemStyle", "編集…"),
                     ("TaskMoveUpMenuItemStyle", "上へ"),
                     ("TaskMoveDownMenuItemStyle", "下へ"),
                 })
        {
            var style = Slice(controls, $"<Style x:Key=\"{key}\"", "</Style>");

            Assert.Contains($"<Setter Property=\"Header\" Value=\"{header}\" />", style, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 利用者に見える文言から内部の言葉と古い書き方が消えている()
    {
        var files = Directory.EnumerateFiles(Path.Combine(SrcDirectory, "Kado.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        foreach (var file in files)
        {
            var text = WithoutComments(File.ReadAllText(file));

            Assert.DoesNotContain("（JSON）", text, StringComparison.Ordinal);
            Assert.DoesNotContain("…（Excel）", text, StringComparison.Ordinal);
            Assert.DoesNotContain("同じ識別子", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ローカルだけの", text, StringComparison.Ordinal);
            Assert.DoesNotContain("実働日ファイルを取り込む", text, StringComparison.Ordinal);
            Assert.DoesNotContain("設定→実働日データ", text, StringComparison.Ordinal);
        }

        Assert.Contains("旧 inaCalendar のバックアップを取り込む…\"", Read("Kado.App/Views/SettingsWindow.xaml"), StringComparison.Ordinal);
        Assert.Contains("ファイルを追加…", Read("Kado.App/Views/EventEditorWindow.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void 実働日計算の節目の操作にツールチップがある()
    {
        var xaml = Read("Kado.App/Views/WorkdayCalculatorWindow.xaml");

        Assert.Matches(@"Content=""▲""[^>]*ToolTip=""上へ""", xaml);
        Assert.Matches(@"Content=""▼""[^>]*ToolTip=""下へ""", xaml);
        Assert.Matches(@"Content=""×""[^>]*ToolTip=""この節目を削除""", xaml);
    }

    [Fact]
    public void ツールバーの送りのツールチップはビューモデルから出す()
    {
        var xaml = WithoutComments(MainXaml);

        Assert.Contains("Command=\"{Binding PreviousCommand}\" ToolTip=\"{Binding PreviousToolTip}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding NextCommand}\" ToolTip=\"{Binding NextToolTip}\"", xaml, StringComparison.Ordinal);
    }

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "Kado.App"))) return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src が見つかりません。");
    }
}
