using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kado.App.Tests;

/// <summary>
/// 画面が固まる・重い処理の直しの配線を、アプリを起動せずに検査する。
/// <para>
/// App プロジェクトは <c>net10.0-windows</c> で、この <c>net10.0</c> のテストからは参照できない
/// （<see cref="ResilienceWiringTests"/> と同じ理由）。実機でしか確かめられない配線は、直した書き方が
/// 実在するか、戻されていないかをソースで見張る。判断そのものは <c>Kado.Presentation.Tests</c> などで試験している。
/// </para>
/// </summary>
public class PerformanceWiringTests
{
    private static string AppDirectory { get; } = Locate();

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(AppDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"{start} が見つかりません。");

        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"{end} が見つかりません。");

        return source[from..to];
    }

    private static string App { get; } = Read("App.xaml.cs");

    // ------------------------------------------------------------------
    // 項目1: shell.log の書き込みは画面のスレッドで同期的に行わない
    // ------------------------------------------------------------------

    [Fact]
    public void shell_logは行列に積むだけで_書き込みは別のスレッドが受け持つ()
    {
        var log = Read("Shell/ShellDiagnosticsLog.cs");

        // 呼んだスレッドでは、ファイルに触れない
        Assert.DoesNotContain("File.AppendAllText", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.CreateDirectory", log, StringComparison.Ordinal);
        Assert.DoesNotContain("new FileInfo", log, StringComparison.Ordinal);
        Assert.Contains("Enqueue(", log, StringComparison.Ordinal);

        // 世代は3つ、1つ1MB
        Assert.Contains("MaxBytes = 1024 * 1024", log, StringComparison.Ordinal);
        Assert.Contains("Generations = 3", log, StringComparison.Ordinal);
    }

    [Fact]
    public void 終わるときと異常終了のときは_shell_logを書き切る()
    {
        // 異常終了：記録とメッセージ表示（止まる）より前に書き切る
        var report = Slice(App, "private static void ReportFatal", "RotateCrashLogIfTooBig();");
        Assert.Contains("ShellDiagnosticsLog.Flush()", report, StringComparison.Ordinal);

        // 正常終了
        var exit = Slice(App, "protected override void OnExit", "base.OnExit(e);");
        Assert.Contains("ShellDiagnosticsLog.Flush()", exit, StringComparison.Ordinal);

        // Windows のシャットダウン（この関数が返るとプロセスが終わる）
        var resolved = Slice(App, "private void OnSessionResolved", "// 他のアプリが取り消した");
        Assert.Contains("ShellDiagnosticsLog.Flush()", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void 窓の移動通知は間引いて書く()
    {
        var host = Read("Shell/AppBarHost.cs");
        var handler = Slice(host, "private void HandleWindowPos", "if (msg != WM_WINDOWPOSCHANGING) return;");

        Assert.Contains("ShellDiagnosticsLog.WriteThrottled(", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellDiagnosticsLog.Write(", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void 開く演出のコマごとの行は間引いて書く()
    {
        var controller = Read("Shell/ShellController.cs");
        var rendering = Slice(controller, "void OnRendering(object? sender, EventArgs e)", "if (t < 1.0) return;");

        Assert.Contains("ShellDiagnosticsLog.WriteThrottled(", rendering, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellDiagnosticsLog.Write(", rendering, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目2: 幅をつまんでいるあいだは保存しない
    // ------------------------------------------------------------------

    [Fact]
    public void 幅の保存は_決まったときの通知で受け_つまむあいだの通知で受けない()
    {
        Assert.Contains("DockWidthCommitted += (_, _) => _shellController?.Save()", App, StringComparison.Ordinal);
        Assert.DoesNotContain("DockWidthChanged += (_, _) => _shellController?.Save()", App, StringComparison.Ordinal);
    }

    [Fact]
    public void つまみの捕捉が奪われたら_離したことにする()
    {
        var xaml = Read("MainWindow.xaml");
        var code = Read("MainWindow.xaml.cs");

        Assert.Contains("LostMouseCapture=\"OnGripCaptureLost\"", xaml, StringComparison.Ordinal);

        var lost = Slice(code, "private void OnGripCaptureLost", "private void OnGripReleased");
        Assert.Contains("vm.Shell.IsResizing = false", lost, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目5: コンバーターはブラシを毎回作らず、辞書の引き当ても使い回す
    // ------------------------------------------------------------------

    [Fact]
    public void コンバーターは色のブラシを自分で作らず_凍結して使い回す入れ物から取る()
    {
        var converters = Read("Converters/Converters.cs");

        Assert.DoesNotContain("new SolidColorBrush", converters, StringComparison.Ordinal);
        Assert.DoesNotContain("ColorConverter.ConvertFromString", converters, StringComparison.Ordinal);
        Assert.Contains("BrushCache.Solid(", converters, StringComparison.Ordinal);
        Assert.Contains("BrushCache.Face(", converters, StringComparison.Ordinal);

        var cache = Read("Converters/BrushCache.cs");
        Assert.Contains(".Freeze()", cache, StringComparison.Ordinal);
    }

    [Fact]
    public void 辞書からの引き当ては_配色を当て直すまで使い回し_当て直したら捨ててから結び直す()
    {
        var converters = Read("Converters/Converters.cs");

        // コンバーターは辞書を直には引かない（ThemeResources 経由。ここだけが TryFindResource を呼ぶ）
        Assert.DoesNotContain("TryFindResource", converters.Replace("<c>TryFindResource</c>", ""), StringComparison.Ordinal);
        Assert.Contains("ThemeResources.Find(", converters, StringComparison.Ordinal);

        // 当て直しの順序：辞書を入れ替える → 覚えたものを捨てる → バインドを結び直す
        var manager = Read("Themes/ThemeManager.cs");
        var swap = manager.IndexOf("merged[0] = rebuilt;", StringComparison.Ordinal);
        var invalidate = manager.IndexOf("ThemeResources.Invalidate()", StringComparison.Ordinal);
        var refresh = manager.IndexOf("ThemeBindingRefresh.RefreshAll()", StringComparison.Ordinal);

        Assert.True(swap >= 0 && invalidate > swap, "辞書を入れ替えたあとで捨てること");
        Assert.True(refresh > invalidate, "捨ててから結び直すこと");
    }

    // ------------------------------------------------------------------
    // 項目4: 一覧ビューの行は、出ている分だけ作る（仮想化）
    // ------------------------------------------------------------------

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument LoadXaml(string relative) => XDocument.Parse(Read(relative));

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);

    [Fact]
    public void 一覧の行は仮想化したパネルに並べ_ScrollViewerはItemsControlのテンプレートの中にある()
    {
        var rows = Named(LoadXaml("Views/AgendaView.xaml"), "Rows");

        Assert.Equal("ItemsControl", rows.Name.LocalName);

        // 仮想化の指定。ピクセル単位のなめらかなスクロールで、入れ物は使い回す
        Assert.Equal("True", (string?)rows.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.Equal("Recycling", (string?)rows.Attribute("VirtualizingPanel.VirtualizationMode"));
        Assert.Equal("Pixel", (string?)rows.Attribute("VirtualizingPanel.ScrollUnit"));
        Assert.Equal("True", (string?)rows.Attribute("ScrollViewer.CanContentScroll"));

        // 並べるパネル
        var panel = rows.Element(Presentation + "ItemsControl.ItemsPanel")!
            .Element(Presentation + "ItemsPanelTemplate")!.Elements().Single();
        Assert.Equal("VirtualizingStackPanel", panel.Name.LocalName);

        // 仮想化は、ScrollViewer が ItemsPresenter を直に包んでいるときだけ効く。
        // ScrollViewer が ItemsControl の外にあると、全行の入れ物を作ってしまう
        var scroller = rows.Element(Presentation + "ItemsControl.Template")!
            .Element(Presentation + "ControlTemplate")!.Elements().Single();
        Assert.Equal("ScrollViewer", scroller.Name.LocalName);
        Assert.Equal("Scroller", (string?)scroller.Attribute(Xaml + "Name"));
        Assert.Equal("ItemsPresenter", scroller.Elements().Single().Name.LocalName);

        Assert.NotEqual("ScrollViewer", rows.Parent!.Name.LocalName);
    }

    [Fact]
    public void 一覧の位置合わせは_入れ物がまだ無い行にも効く()
    {
        var code = Read("Views/AgendaView.xaml.cs");

        // 仮想化すると、見えていない行の入れ物は無い。作らせてから測る
        Assert.Contains("BringIndexIntoViewPublic(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ContainerFromItem(", code, StringComparison.Ordinal);
        Assert.Contains("Template?.FindName(\"Scroller\", Rows)", code, StringComparison.Ordinal);

        // 作ってある行にまで頼むと、上にはみ出させて置いた位置を戻してしまう。無いときだけ頼む
        var align = Slice(code, "private AlignResult TryAlign", "private static int IndexOf");
        Assert.Contains("ContainerFromIndex(index) is null", align, StringComparison.Ordinal);

        // 行が丸ごと入れ替わると仮想化したパネルは先頭へ戻る。見ていた位置へ戻す
        Assert.Contains("nameof(AgendaViewModel.Rows)", code, StringComparison.Ordinal);
        Assert.Contains("_anchorDate", code, StringComparison.Ordinal);

        // 控えてある位置合わせ（C-1）はそのまま
        Assert.Contains("TakePendingScroll()", code, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目6: 月のマスは今の形だけを作る
    // ------------------------------------------------------------------

    private static XElement Template(XDocument document, string key) =>
        document.Descendants(Presentation + "DataTemplate")
            .Single(e => (string?)e.Attribute(Xaml + "Key") == key);

    [Fact]
    public void 月のマスはふつうの形と詰めた形を別のテンプレートにして_今の形だけを作る()
    {
        var document = LoadXaml("Views/MonthView.xaml");
        var full = Template(document, "MonthCellFullTemplate");
        var compact = Template(document, "MonthCellCompactTemplate");

        // ふつうの形に詰めた形の中身は無く、詰めた形にふつうの形の中身は無い
        var fullNames = full.Descendants().Select(e => (string?)e.Attribute(Xaml + "Name")).OfType<string>().ToArray();
        var compactNames = compact.Descendants().Select(e => (string?)e.Attribute(Xaml + "Name")).OfType<string>().ToArray();

        Assert.Contains("DayNumber", fullNames);
        Assert.DoesNotContain("CompactNumber", fullNames);
        Assert.Contains("CompactNumber", compactNames);
        Assert.DoesNotContain("DayNumber", compactNames);
        Assert.DoesNotContain("DayNumberFace", compactNames);

        // 片方を Collapsed にして両方作る形に戻していない
        Assert.DoesNotContain(full.Descendants(), e => (string?)e.Attribute("Visibility") == "Collapsed");
        Assert.DoesNotContain(compact.Descendants(), e => (string?)e.Attribute("Visibility") == "Collapsed");
    }

    [Fact]
    public void 月のマスの切り替えは_ItemsControlの1か所で行い_マスごとにトリガーを持たない()
    {
        var document = LoadXaml("Views/MonthView.xaml");

        // マスのテンプレートの中には IsCompact を結ばない（42マスぶんの RelativeSource つきトリガーを持たない）
        foreach (var key in new[] { "MonthCellFullTemplate", "MonthCellCompactTemplate" })
        {
            var template = Template(document, key);
            Assert.DoesNotContain(
                template.Descendants().SelectMany(e => e.Attributes()),
                a => a.Value.Contains("IsCompact", StringComparison.Ordinal));
        }

        // 切り替えは CellsHost の Style 1か所
        var host = Named(document, "CellsHost");
        Assert.Null(host.Attribute("ItemTemplate"));   // 局所の値は Style の Setter より強い。置かない

        var style = host.Element(Presentation + "ItemsControl.Style")!.Element(Presentation + "Style")!;
        var trigger = style.Element(Presentation + "Style.Triggers")!.Elements(Presentation + "DataTrigger").Single();

        Assert.Equal("{Binding IsCompact}", (string?)trigger.Attribute("Binding"));
        Assert.Equal("True", (string?)trigger.Attribute("Value"));
        Assert.Contains("MonthCellCompactTemplate", (string?)trigger.Element(Presentation + "Setter")!.Attribute("Value"));
        Assert.Contains("MonthCellFullTemplate", (string?)style.Elements(Presentation + "Setter").Single().Attribute("Value"));
    }

    [Theory]
    [InlineData("MonthCellFullTemplate")]
    [InlineData("MonthCellCompactTemplate")]
    public void 月のマスのトリガーが指す名前は_同じテンプレートの中に実在する(string key)
    {
        var template = Template(LoadXaml("Views/MonthView.xaml"), key);

        var names = template.Descendants().Select(e => (string?)e.Attribute(Xaml + "Name")).OfType<string>().ToHashSet();
        var targets = template.Descendants()
            .Select(e => (string?)e.Attribute("TargetName") ?? (string?)e.Attribute("SourceName"))
            .OfType<string>()
            .Distinct()
            .ToArray();

        Assert.NotEmpty(targets);
        Assert.All(targets, t => Assert.Contains(t, names));
    }

    [Theory]
    [InlineData("MonthCellFullTemplate")]
    [InlineData("MonthCellCompactTemplate")]
    public void 月のマスの操作は両方の形で同じ受け手に届く(string key)
    {
        // ドラッグ・ダブルクリック・落とし先の表示は、コードビハインドで受ける（InputBinding は祖先をたどれない）
        var cell = Template(LoadXaml("Views/MonthView.xaml"), key)
            .Descendants(Presentation + "Border")
            .Single(e => (string?)e.Attribute(Xaml + "Name") == "Cell");

        Assert.Equal("OnCellClicked", (string?)cell.Attribute("MouseLeftButtonDown"));
        Assert.Equal("True", (string?)cell.Attribute("AllowDrop"));
        Assert.Equal("OnCellDragOver", (string?)cell.Attribute("DragOver"));
        Assert.Equal("OnCellDragLeft", (string?)cell.Attribute("DragLeave"));
        Assert.Equal("OnCellDropped", (string?)cell.Attribute("Drop"));
    }

    [Fact]
    public void 月のマスのふつうの形は_予定_タスク_帯_マイルストーンの操作を今までどおり受ける()
    {
        var xaml = Read("Views/MonthView.xaml");

        // 予定・帯・タスク・マイルストーンのチップは、共有のテンプレート（変えていない）から出る
        var full = Template(LoadXaml("Views/MonthView.xaml"), "MonthCellFullTemplate").ToString();

        Assert.Contains("EventBandSlotTemplate", full, StringComparison.Ordinal);
        Assert.Contains("EventChipTemplate", full, StringComparison.Ordinal);
        Assert.Contains("TaskChipTemplate", full, StringComparison.Ordinal);
        Assert.Contains("MilestoneTagTemplate", full, StringComparison.Ordinal);

        foreach (var handler in new[] { "OnEventChipClicked", "OnTaskChipClicked", "OnMilestoneClicked", "OnChipDragging" })
        {
            Assert.Contains($"=\"{handler}\"", xaml, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------
    // 項目10: 起動時の窓の位置は、控えた位置の画面の倍率で物理ピクセルへ直す
    // ------------------------------------------------------------------

    [Fact]
    public void 控えた窓の位置は_窓の倍率で一律に直さず_控えた位置の画面の倍率で直す()
    {
        var window = Read("MainWindow.xaml.cs");
        var fit = Slice(window, "private WindowPlacement FitToScreens", "/// <summary>いまの置き場所を控える。</summary>");

        Assert.Contains("ShellGeometry.ResolveSavedWindow(", fit, StringComparison.Ordinal);

        // 窓の倍率（出す前は主画面のもの）を掛けて直す書き方に戻していない
        Assert.DoesNotContain("placement.Left * scale", fit, StringComparison.Ordinal);
        Assert.DoesNotContain("placement.Width) * scale", fit, StringComparison.Ordinal);

        // 置き直したときは、置いた先の画面の倍率で DIP に戻す
        Assert.Contains("screens[fit.ScreenIndex].Scale", fit, StringComparison.Ordinal);
    }

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
