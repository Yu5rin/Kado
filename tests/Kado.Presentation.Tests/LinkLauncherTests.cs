using System.ComponentModel;
using Kado.Data.Models;
using Kado.Presentation.Links;

namespace Kado.Presentation.Tests;

/// <summary>
/// 開く処理。判断（何を開いてよいか）は <see cref="LinkRules"/> と <see cref="PathLaunchPlanner"/> が持ち、
/// ここは通ったものだけを <see cref="ILinkOpener"/> に渡すことを見る。
/// </summary>
public class LinkLauncherTests
{
    private static LinkLauncher Create(
        FakeLinkOpener opener, IEnumerable<string>? files = null, IEnumerable<string>? folders = null)
    {
        var fileSet = (files ?? []).ToHashSet(StringComparer.Ordinal);
        var folderSet = (folders ?? []).ToHashSet(StringComparer.Ordinal);

        return new LinkLauncher(opener, fileSet.Contains, folderSet.Contains);
    }

    private static EventAttachment Attachment(string url) => new("id", url, "資料.pdf", "application/pdf");

    // ------------------------------------------------------------------
    // URL
    // ------------------------------------------------------------------

    [Fact]
    public void リンクはブラウザに渡す形で開く()
    {
        var opener = new FakeLinkOpener();

        var result = Create(opener).OpenWeb("  https://example.com/spec  ");

        Assert.True(result.Opened);
        Assert.Null(result.Message);
        Assert.Equal([new LaunchRequest(LaunchKind.Shell, "https://example.com/spec")], opener.Launched);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("www.example.com")]
    [InlineData(null)]
    public void http以外のリンクは起動を頼まず理由を返す(string? url)
    {
        var opener = new FakeLinkOpener();

        var result = Create(opener).OpenWeb(url);

        Assert.False(result.Opened);
        Assert.Contains("http", result.Message, StringComparison.Ordinal);
        Assert.Empty(opener.Launched);
    }

    [Fact]
    public void 予定の添付はhttpsだけ開ける()
    {
        var opener = new FakeLinkOpener();
        var launcher = Create(opener);

        Assert.True(launcher.OpenAttachment(Attachment("https://drive.google.com/file/d/1/view")).Opened);

        var refused = launcher.OpenAttachment(Attachment("http://drive.google.com/file/d/1/view"));

        Assert.False(refused.Opened);
        Assert.Equal("この添付は開けません（https の URL ではありません）", refused.Message);
        Assert.Single(opener.Launched);
    }

    [Fact]
    public void 起動に失敗したら例外にせず理由を返す()
    {
        var opener = new FakeLinkOpener { Throws = new Win32Exception("既定のブラウザがありません") };

        var result = Create(opener).OpenWeb("https://example.com/");

        Assert.False(result.Opened);
        Assert.Contains("既定のブラウザがありません", result.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // ファイルの場所
    // ------------------------------------------------------------------

    [Fact]
    public async Task ファイルをシェルで開く()
    {
        var opener = new FakeLinkOpener();

        var result = await Create(opener, files: [@"C:\資料\図面.pdf"]).OpenPathAsync(@"C:\資料\図面.pdf");

        Assert.True(result.Opened);
        Assert.Null(result.Message);
        Assert.Equal([new LaunchRequest(LaunchKind.Shell, @"C:\資料\図面.pdf")], opener.Launched);
    }

    [Fact]
    public async Task フォルダを開く()
    {
        var opener = new FakeLinkOpener();

        var result = await Create(opener, folders: [@"C:\資料"]).OpenPathAsync(@"C:\資料");

        Assert.True(result.Opened);
        Assert.Equal([new LaunchRequest(LaunchKind.Shell, @"C:\資料")], opener.Launched);
    }

    [Fact]
    public async Task 見つからなければ開かず見つかりませんと返す()
    {
        var opener = new FakeLinkOpener();

        var result = await Create(opener).OpenPathAsync(@"C:\消えた\図面.pdf");

        Assert.False(result.Opened);
        Assert.Equal(@"見つかりません：C:\消えた\図面.pdf", result.Message);
        Assert.Empty(opener.Launched);
    }

    [Fact]
    public async Task 実行形式は実行せずフォルダを開いて選んだ状態にする()
    {
        var opener = new FakeLinkOpener();

        var result = await Create(opener, files: [@"C:\tools\setup.exe"]).OpenPathAsync(@"C:\tools\setup.exe");

        // 開けた（フォルダを開いた）ので成功。ただし実行しなかったことを伝える
        Assert.True(result.Opened);
        Assert.NotNull(result.Message);
        Assert.Equal([new LaunchRequest(LaunchKind.Reveal, @"C:\tools\setup.exe")], opener.Launched);
    }

    [Fact]
    public async Task フルパスでないものは開かない()
    {
        var opener = new FakeLinkOpener();

        var result = await Create(opener, files: [@"図面.pdf"]).OpenPathAsync(@"図面.pdf");

        Assert.False(result.Opened);
        Assert.Empty(opener.Launched);
    }

    [Fact]
    public async Task 存在の確認は呼んだスレッドで行わない()
    {
        // 共有フォルダの先が落ちていると、File.Exists は数十秒返らない。画面のスレッドで
        // 呼ぶと本体ごと固まるので、確認は別のスレッドに出す
        var callerThread = Environment.CurrentManagedThreadId;
        int? probedOn = null;

        var launcher = new LinkLauncher(
            new FakeLinkOpener(),
            fileExists: _ =>
            {
                probedOn = Environment.CurrentManagedThreadId;
                return true;
            },
            directoryExists: _ => false);

        await launcher.OpenPathAsync(@"C:\資料\図面.pdf");

        Assert.NotNull(probedOn);
        Assert.NotEqual(callerThread, probedOn);
    }

    [Fact]
    public async Task ファイルの起動に失敗したら例外にせず理由を返す()
    {
        var opener = new FakeLinkOpener { Throws = new Win32Exception("関連付けがありません") };

        var result = await Create(opener, files: [@"C:\資料\図面.xyz"]).OpenPathAsync(@"C:\資料\図面.xyz");

        Assert.False(result.Opened);
        Assert.Contains("関連付けがありません", result.Message, StringComparison.Ordinal);
    }
}
