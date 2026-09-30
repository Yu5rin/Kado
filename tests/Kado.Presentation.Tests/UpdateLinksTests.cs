using System.Text.RegularExpressions;
using Kado.Presentation.Update;

namespace Kado.Presentation.Tests;

/// <summary>
/// 更新の問い合わせ先と配布物の場所の組み立て。
/// <para>
/// API が上限（403）で使えないとき、更新は<b>ここで組み立てた URL</b>だけを頼りにする。
/// 組み立てを間違えると 404 になるか、意図しない場所から取ることになる。
/// </para>
/// </summary>
public class UpdateLinksTests
{
    private const string ApiUrl = "https://api.github.com/repos/Yu5rin/Kado/releases/latest";
    private const string AtomUrl = "https://github.com/Yu5rin/Kado/releases.atom";

    [Fact]
    public void APIのURLからAtomのURLを組み立てられる()
    {
        Assert.Equal(AtomUrl, UpdateLinks.TryBuildAtomUrl(ApiUrl));
    }

    [Theory]
    [InlineData("http://api.github.com/repos/Yu5rin/Kado/releases/latest")]   // 暗号化されていない
    [InlineData("https://example.com/repos/Yu5rin/Kado/releases/latest")]     // GitHub 以外
    [InlineData("https://api.github.com.evil.example/repos/a/b/releases/latest")]
    [InlineData("https://api.github.com/users/Yu5rin")]                       // repos で始まらない
    [InlineData("https://api.github.com/repos/Yu5rin")]                       // 途中まで
    [InlineData("これはURLではない")]
    [InlineData("")]
    [InlineData(null)]
    public void 組み立てられない形ならnull(string? apiUrl)
    {
        // GitHub 以外の配布元なら、Atom は使わず API だけで確かめる
        Assert.Null(UpdateLinks.TryBuildAtomUrl(apiUrl));
    }

    [Fact]
    public void ダウンロードURLをAPIなしで組み立てられる()
    {
        Assert.Equal(
            "https://github.com/Yu5rin/Kado/releases/download/v1.0.8/Kado-1.0.8-win-x64.exe",
            UpdateLinks.TryBuildDownloadUrl(AtomUrl, "v1.0.8"));
    }

    [Fact]
    public void 組み立てたURLは許可された取得先の検査を通る()
    {
        // 落としたものをそのまま実行するので、組み立てた URL も「HTTPS で GitHub の配信先」
        // であることを確かめる（UpdateService は取りに行く前にこの検査を通す）
        var url = UpdateLinks.TryBuildDownloadUrl(AtomUrl, "v1.0.8");

        Assert.True(ReleaseFeed.IsAllowedDownloadUrl(url));
        Assert.True(ReleaseFeed.IsAllowedDownloadUrl(UpdateLinks.BuildReleasePageUrl(AtomUrl, "v1.0.8")));
        Assert.True(ReleaseFeed.IsAllowedDownloadUrl(UpdateLinks.BuildLatestReleasePageUrl(AtomUrl)));
    }

    [Fact]
    public void 配布物の名前は版から決まる()
    {
        Assert.Equal("Kado-1.0.8-win-x64.exe", UpdateLinks.BuildAssetFileName("v1.0.8"));
        Assert.Equal("Kado-1.0.8-win-x64.exe", UpdateLinks.BuildAssetFileName("1.0.8"));
    }

    [Fact]
    public void 配布物の名前はリリースのワークフローが作る名前と揃っている()
    {
        // .github/workflows/release.yml の「$exe = "Kado-…-win-x64.exe"」と食い違うと、
        // API が使えないときに組み立てた URL が 404 になり、更新できなくなる
        var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "release.yml"));

        var match = Regex.Match(workflow, "\\$exe = \"(?<name>[^\"]+)\"");
        Assert.True(match.Success, "release.yml に $exe の定義が見つかりません");

        var fromWorkflow = match.Groups["name"].Value.Replace("${{ steps.version.outputs.value }}", "1.2.3");

        Assert.Equal(fromWorkflow, UpdateLinks.BuildAssetFileName("v1.2.3"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("v1.0")]
    [InlineData("v1.0.0-beta")]
    public void タグが版の表記でなければ組み立てない(string? tag)
    {
        Assert.Null(UpdateLinks.TryBuildDownloadUrl(AtomUrl, tag));
        Assert.Equal(string.Empty, UpdateLinks.BuildReleasePageUrl(AtomUrl, tag));
    }

    [Theory]
    [InlineData("https://example.com/feed")]
    [InlineData("https://github.com/Yu5rin/Kado/releases")]   // .atom で終わらない
    [InlineData("")]
    [InlineData(null)]
    public void Atom以外のURLからは組み立てない(string? atomUrl)
    {
        Assert.Null(UpdateLinks.TryBuildDownloadUrl(atomUrl, "v1.0.8"));
        Assert.Equal(string.Empty, UpdateLinks.BuildReleasePageUrl(atomUrl, "v1.0.8"));
        Assert.Equal(string.Empty, UpdateLinks.BuildLatestReleasePageUrl(atomUrl));
    }

    [Fact]
    public void いちばん新しいリリースのページを組み立てられる()
    {
        Assert.Equal("https://github.com/Yu5rin/Kado/releases/latest", UpdateLinks.BuildLatestReleasePageUrl(AtomUrl));
    }

    [Theory]
    [InlineData("v1.0.8", 1, 0, 8)]
    [InlineData("v2.10.0", 2, 10, 0)]
    public void タグから版を読める(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateLinks.TryParseTag(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public void 版は数として比べる()
    {
        // 文字で比べると 1.0.10 が 1.0.9 より古くなり、更新が止まる
        Assert.True(UpdateLinks.TryParseTag("v1.0.10", out var newer));
        Assert.True(UpdateLinks.TryParseTag("v1.0.9", out var older));

        Assert.True(newer > older);
    }

    /// <summary>テストの実行場所から、リポジトリの根（<c>Kado.sln</c> があるところ）を探す。</summary>
    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Kado.sln"))) return dir.FullName;
        }

        throw new DirectoryNotFoundException("リポジトリの根（Kado.sln）が見つかりません。");
    }
}
