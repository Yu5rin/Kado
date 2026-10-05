using Kado.Data.Models;

namespace Kado.Data.Tests;

/// <summary>
/// タスクに添える「ファイルの場所」。場所（フルパス）だけを持ち、JSON 配列で保存する。
/// </summary>
public class TaskAttachmentTests
{
    [Theory]
    [InlineData(@"C:\資料\図面.pdf", "図面.pdf")]
    [InlineData(@"C:\資料\設計フォルダ\", "設計フォルダ")]
    [InlineData(@"\\server\share\議事録\2026-09.xlsx", "2026-09.xlsx")]
    [InlineData(@"\\server\share", "share")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("C:/資料/図面.pdf", "図面.pdf")]
    public void 表示名はパスの最後の部分(string path, string expected)
    {
        Assert.Equal(expected, new TaskAttachment(path).Name);
    }

    [Fact]
    public void JSONと一覧を行き来できる()
    {
        var list = new[]
        {
            new TaskAttachment(@"C:\資料\図面.pdf"),
            new TaskAttachment(@"\\server\share\議事録"),
        };

        var json = TaskAttachments.ToJson(list);

        Assert.Equal(list, TaskAttachments.Read(json));

        // 日本語は \uXXXX に直さず、そのまま読める形で持つ
        Assert.Contains("図面.pdf", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 空なら空の配列ではなくnullにする()
    {
        // 「持っていない」は NULL で表す。列に "[]" を残さない
        Assert.Null(TaskAttachments.ToJson([]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("これはJSONではない")]
    [InlineData("""{"path":"C:\\a"}""")]
    [InlineData("""["C:\\a"]""")]
    [InlineData("""[{"name":"pathが無い"}]""")]
    [InlineData("""[{"path":""}]""")]
    [InlineData("""[{"path":42}]""")]
    public void 読めない文字列は空の一覧として読む(string? json)
    {
        // 例外にすると、そのタスクを開けなくなる
        Assert.Empty(TaskAttachments.Read(json));
    }

    [Fact]
    public void 読めるものだけを拾う()
    {
        var read = TaskAttachments.Read("""[{"path":"C:\\a.txt"},{"x":1},"文字列",{"path":"C:\\b.txt"}]""");

        Assert.Equal([new TaskAttachment(@"C:\a.txt"), new TaskAttachment(@"C:\b.txt")], read);
    }

    [Fact]
    public void 同じ場所は足さない()
    {
        var current = TaskAttachments.Add([], [@"C:\資料\図面.pdf"]);

        // 大文字小文字・末尾の区切りの違いは同じ場所。同じ呼び出しの中の重複も一度だけ
        var added = TaskAttachments.Add(current, [@"c:\資料\図面.PDF", @"C:\資料\", @"C:\資料", @"C:\資料\"]);

        Assert.Equal([@"C:\資料\図面.pdf", @"C:\資料\"], added.Select(a => a.Path));
    }

    [Fact]
    public void 空の文字列は足さない()
    {
        var added = TaskAttachments.Add([], ["", "   ", @"  C:\a.txt  "]);

        Assert.Equal([@"C:\a.txt"], added.Select(a => a.Path));
    }

    [Fact]
    public void 外せる()
    {
        var current = TaskAttachments.Add([], [@"C:\a.txt", @"C:\b.txt"]);

        var remaining = TaskAttachments.Remove(current, @"c:\A.TXT");

        Assert.Equal([@"C:\b.txt"], remaining.Select(a => a.Path));
    }

    [Fact]
    public void ルートの区切りは落とさない()
    {
        // "C:" はカレントディレクトリを指す別の場所
        Assert.False(TaskAttachments.SamePlace(@"C:\", "C:"));
        Assert.True(TaskAttachments.SamePlace(@"C:\", @"c:\"));
    }
}
