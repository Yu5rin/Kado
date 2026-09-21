using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 実働日データの配信用書き出し（feed.json）。
/// <para>
/// 直書きだと、途中まで書かれたファイルを他の端末が取りに行って失敗しうる。
/// 一時ファイルへ書いてから置き換える形になっているかを確かめる（項目6）。
/// </para>
/// </summary>
public class ExportFeedCommandTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}.json");

    [Fact]
    public void 書き出すと本体ができて一時ファイルは残らない()
    {
        using var test = TestWorkspace.Create();
        var path = TempPath("feed");
        var files = new FakeFileDialogs { FileToSave = path };

        try
        {
            var main = new MainViewModel(test.Workspace, Today, files: files);
            main.ExportWorkingDayFeedCommand.Execute(null);

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Contains("書き出しました", main.StatusMessage);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void 実働日データが無ければ書き出せないと伝える()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var path = TempPath("feed");
        var files = new FakeFileDialogs { FileToSave = path };

        var main = new MainViewModel(test.Workspace, Today, files: files);
        main.ExportWorkingDayFeedCommand.Execute(null);

        Assert.False(File.Exists(path));
        Assert.Contains("書き出せる実働日データがありません", main.StatusMessage);
    }

    [Fact]
    public void 取り消したら何もしない()
    {
        using var test = TestWorkspace.Create();
        var files = new FakeFileDialogs { FileToSave = null };

        var main = new MainViewModel(test.Workspace, Today, files: files);
        main.ExportWorkingDayFeedCommand.Execute(null);

        Assert.Null(main.StatusMessage);
    }
}
