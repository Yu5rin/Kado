using System.Text.RegularExpressions;

namespace Kado.App.Tests;

/// <summary>
/// 編集画面・ドラッグ・通知・文言の直しを、アプリを起動せずにソースで見張る（WPF は Linux では動かせない）。
/// </summary>
public class EditorAndDragWiringTests
{
    private static string SrcDirectory { get; } = Locate();

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(SrcDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Kado.App")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new DirectoryNotFoundException("src ディレクトリが見つかりません。")
            : Path.Combine(directory.FullName, "src");
    }

    [Fact]
    public void ドラッグは左ボタンが離れていれば掴みかけを捨てる()
    {
        var code = Read("Kado.App/Views/DragSession.cs");

        Assert.Matches(
            new Regex(@"LeftButton\s*!=\s*MouseButtonState\.Pressed\s*\)\s*\{\s*(//[^\n]*\s*)*_candidate\s*=\s*null;", RegexOptions.Singleline),
            code);
    }

    [Fact]
    public void ドラッグは押した要素と同じ予定のときだけ始める()
    {
        var code = Read("Kado.App/Views/DragSession.cs");

        Assert.Contains("ReferenceEquals(current, _candidate)", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Kado.App/Views/EventEditorWindow.xaml.cs")]
    [InlineData("Kado.App/Views/TaskEditorWindow.xaml.cs")]
    public void 保存の先頭で日付欄の打ちかけを確定させる(string file)
    {
        var code = Read(file);

        var save = code.IndexOf("private void Save()", StringComparison.Ordinal);
        Assert.True(save >= 0);

        var commit = code.IndexOf("EditorWindowGuard.CommitDatePicker(this)", save, StringComparison.Ordinal);
        var check = code.IndexOf("CanSave", save, StringComparison.Ordinal);

        Assert.True(commit > save, "Save の中で CommitDatePicker を呼ぶこと。");
        Assert.True(commit < check, "CanSave を見る前に確定させること。");
    }

    [Fact]
    public void 日付欄の確定はDatePickerの親をTreeWalkでたどる()
    {
        var code = Read("Kado.App/Views/EditorWindowGuard.cs");

        Assert.Contains("TreeWalk.ParentOf", code, StringComparison.Ordinal);
        Assert.DoesNotContain("VisualTreeHelper.GetParent", code, StringComparison.Ordinal);
    }

    [Fact]
    public void 通知を押したら本体を前に出す経路を通す()
    {
        var code = Read("Kado.App/Views/ToastWindow.xaml.cs");

        Assert.Contains("App)?.BringToFront()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("main.Activate()", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Kado.App/Views/AgendaView.xaml")]
    [InlineData("Kado.App/Views/DayPaneView.xaml")]
    [InlineData("Kado.App/Views/WeekView.xaml")]
    [InlineData("Kado.App/Views/MonthView.xaml")]
    [InlineData("Kado.App/Views/DayView.xaml")]
    public void 繰り返しの印は文字でなくアイコンで出す(string file)
    {
        var xaml = Read(file);

        Assert.DoesNotContain("RepeatMark", xaml, StringComparison.Ordinal);
        Assert.Contains("IconRepeat", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 画面の文言に歯車や絵文字を使わない()
    {
        var offenders = new List<string>();

        var files = Directory.EnumerateFiles(Path.Combine(SrcDirectory, "Kado.App"), "*.xaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(SrcDirectory, "Kado.Presentation"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);

            // コメントは対象外。文言（属性値・文字列）だけを見る
            text = file.EndsWith(".xaml", StringComparison.Ordinal)
                ? Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline)
                : Regex.Replace(text, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);

            if (Regex.IsMatch(text, "[⚙↻️]|[\uD83C-\uD83E][\uDC00-\uDFFF]"))
            {
                offenders.Add(Path.GetRelativePath(SrcDirectory, file));
            }
        }

        Assert.Empty(offenders);
    }
}
