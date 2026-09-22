using Kado.Presentation.Update;

namespace Kado.Presentation.Tests;

/// <summary>
/// リリースの応答の読み取り。
/// <para>
/// ここで返した行き先から実行ファイルを落として<b>そのまま動かす</b>。判断を誤ると、
/// 意図しない場所から取ってきたものを実行することになる。
/// </para>
/// </summary>
public class ReleaseFeedTests
{
    private const string Typical = """
        {
          "tag_name": "v0.5.0",
          "html_url": "https://github.com/Yu5rin/SlideinaCalendar/releases/tag/v0.5.0",
          "body": "同期を直しました",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "Kado-0.5.0-win-x64.exe",
              "size": 71662439,
              "digest": "sha256:ABCDEF0123456789",
              "browser_download_url":
                "https://github.com/Yu5rin/SlideinaCalendar/releases/download/v0.5.0/Kado.exe"
            }
          ]
        }
        """;

    [Fact]
    public void ふつうの応答を読める()
    {
        var info = Assert.IsType<UpdateInfo>(ReleaseFeed.Parse(Typical));

        Assert.Equal(new Version(0, 5, 0), info.Version);
        Assert.Equal("v0.5.0", info.TagName);
        Assert.Equal(71662439, info.SizeBytes);
        Assert.Equal("ABCDEF0123456789", info.Sha256);
        Assert.Contains("同期を直しました", info.ReleaseNotes, StringComparison.Ordinal);
    }

    [Fact]
    public void 版は数として比べる()
    {
        // 文字で比べると 0.4.10 が 0.4.9 より古いことになり、更新が止まる
        Assert.True(ReleaseFeed.TryParseVersion("v0.4.10", out var newer));
        Assert.True(ReleaseFeed.TryParseVersion("v0.4.9", out var older));

        Assert.True(newer > older);
    }

    [Theory]
    [InlineData("v0.5.0", 0, 5, 0)]
    [InlineData("0.5.0", 0, 5, 0)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("v0.5.0-beta", 0, 5, 0)]
    [InlineData("v0.5.0+build7", 0, 5, 0)]
    public void いろいろな書き方のタグを読める(string tag, int major, int minor, int build)
    {
        Assert.True(ReleaseFeed.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("最新版")]
    [InlineData("v")]
    public void 読めないタグは断る(string? tag)
    {
        Assert.False(ReleaseFeed.TryParseVersion(tag, out _));
    }

    [Fact]
    public void 同じ版や古い版は入れ替えない()
    {
        var info = ReleaseFeed.Parse(Typical)!;

        Assert.False(ReleaseFeed.IsNewerThan(info, new Version(0, 5, 0)));
        Assert.False(ReleaseFeed.IsNewerThan(info, new Version(0, 6, 0)));
        Assert.True(ReleaseFeed.IsNewerThan(info, new Version(0, 4, 9)));
    }

    [Fact]
    public void 下書きは配らない()
    {
        Assert.Null(ReleaseFeed.Parse(Typical.Replace("\"draft\": false", "\"draft\": true")));
    }

    [Fact]
    public void 事前公開は配らない()
    {
        Assert.Null(ReleaseFeed.Parse(Typical.Replace("\"prerelease\": false", "\"prerelease\": true")));
    }

    [Theory]
    [InlineData("https://github.com/Yu5rin/x/releases/download/v1/a.exe")]
    [InlineData("https://api.github.com/repos/Yu5rin/x/releases/assets/1")]
    [InlineData("https://objects.githubusercontent.com/abc")]
    [InlineData("https://release-assets.githubusercontent.com/abc")]
    public void GitHubからなら取りに行く(string url)
    {
        Assert.True(ReleaseFeed.IsAllowedDownloadUrl(url));
    }

    [Theory]
    [InlineData("http://github.com/a.exe")]                       // 暗号化されていない
    [InlineData("https://github.com.example.com/a.exe")]          // 似せた別の場所
    [InlineData("https://evil.example.com/a.exe")]
    [InlineData("https://githubusercontent.com.evil.jp/a.exe")]
    [InlineData("file:///C:/windows/system32/cmd.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void それ以外からは取りに行かない(string? url)
    {
        // 落としたものをそのまま実行する。応答に書かれた行き先を鵜呑みにしない
        Assert.False(ReleaseFeed.IsAllowedDownloadUrl(url));
    }

    [Fact]
    public void 行き先が許されない応答は丸ごと断る()
    {
        var tampered = Typical.Replace(
            "https://github.com/Yu5rin/SlideinaCalendar/releases/download/v0.5.0/Kado.exe",
            "https://evil.example.com/Kado.exe");

        Assert.Null(ReleaseFeed.Parse(tampered));
    }

    [Fact]
    public void 実行ファイル以外は選ばない()
    {
        var zipOnly = Typical.Replace(
            "Kado-0.5.0-win-x64.exe", "Kado-0.5.0-win-x64.zip");

        Assert.Null(ReleaseFeed.Parse(zipOnly));
    }

    [Fact]
    public void ハッシュが無くても読める()
    {
        var noDigest = Typical.Replace("\"digest\": \"sha256:ABCDEF0123456789\",", string.Empty);

        var info = Assert.IsType<UpdateInfo>(ReleaseFeed.Parse(noDigest));
        Assert.Null(info.Sha256);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("これは JSON ではありません")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"tag_name":"v0.5.0"}""")]
    [InlineData("""{"tag_name":"v0.5.0","assets":[]}""")]
    public void 読めない応答では何も返さない(string json)
    {
        // 読めないときに古い版を「新しい」と誤判定すると、無限に入れ替えようとする
        Assert.Null(ReleaseFeed.Parse(json));
    }
}
