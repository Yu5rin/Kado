using Kado.Presentation.Settings;
using Kado.Presentation.Update;

namespace Kado.Presentation.Tests;

/// <summary>入れ替え待ちの控え。落としたものをそのまま実行するので、使ってよいかの判定を固定する。</summary>
public class StagedUpdateTests
{
    private const string Folder = "/data/Kado/updates";

    private static readonly Version Current = new(1, 1, 1);

    private static StagedUpdate Staged(
        string tag = "v1.1.2", string? path = null, string sha = "ABCDEF") =>
        new(tag, path ?? $"{Folder}/Kado-{tag}.exe", sha, "https://github.com/Yu5rin/Kado/releases/tag/" + tag, "同期を直しました");

    private static StagedVerdict Evaluate(
        StagedUpdate staged, bool exists = true, string actualSha = "abcdef", Version? current = null) =>
        StagedUpdateCheck.Evaluate(staged, current ?? Current, Folder, _ => exists, _ => actualSha);

    [Fact]
    public void 版とファイルの場所とSHA256が揃っていれば使える()
    {
        Assert.Equal(StagedVerdict.Usable, Evaluate(Staged()));
    }

    [Fact]
    public void 控えの版がいまの版以下なら捨てる()
    {
        // 入れ替えが済んだあと（同じ版）も、古い控えが残っていた（小さい版）も
        Assert.Equal(StagedVerdict.NotNewer, Evaluate(Staged("v1.1.2"), current: new Version(1, 1, 2)));
        Assert.Equal(StagedVerdict.NotNewer, Evaluate(Staged("v1.1.0")));
    }

    [Fact]
    public void 版は数として比べる()
    {
        Assert.Equal(StagedVerdict.Usable, Evaluate(Staged("v1.1.10"), current: new Version(1, 1, 9)));
        Assert.Equal(StagedVerdict.NotNewer, Evaluate(Staged("v1.1.9"), current: new Version(1, 1, 10)));
    }

    [Fact]
    public void ファイルが無ければ使えない()
    {
        Assert.Equal(StagedVerdict.FileMissing, Evaluate(Staged(), exists: false));
    }

    [Fact]
    public void ハッシュが合わなければ使えない()
    {
        Assert.Equal(StagedVerdict.ShaMismatch, Evaluate(Staged(), actualSha: "000000"));
    }

    [Fact]
    public void 控えにハッシュが無ければ照合できないので使えない()
    {
        Assert.Equal(StagedVerdict.ShaMissing, Evaluate(Staged(sha: "")));
    }

    [Theory]
    [InlineData("/etc/Kado-v1.1.2.exe")]
    [InlineData("/data/Kado/updates/../data.db")]
    [InlineData("/data/Kado/updates-evil/Kado-v1.1.2.exe")]
    [InlineData("")]
    public void 決められた置き場所の外のファイルは使わない(string path)
    {
        Assert.Equal(StagedVerdict.OutsideFolder, Evaluate(Staged(path: path)));
    }

    [Fact]
    public void 版が読めない控えは使わない()
    {
        Assert.Equal(StagedVerdict.NotNewer, Evaluate(Staged("nightly")));
    }

    [Fact]
    public void 設定に保存する形に往復できる()
    {
        var staged = Staged();

        var read = StagedUpdate.TryParse(staged.ToJson());

        Assert.Equal(staged, read);
        Assert.Equal(new Version(1, 1, 2), read!.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("これは JSON ではない")]
    [InlineData("{}")]
    [InlineData("{\"Tag\":\"nightly\",\"FilePath\":\"a\",\"Sha256\":\"b\",\"ReleaseUrl\":\"\",\"ReleaseNotes\":\"\"}")]
    public void 壊れた控えは読まない(string? json)
    {
        Assert.Null(StagedUpdate.TryParse(json));
    }

    [Fact]
    public void 見つけた版から控えを作れる_本文は上限で切る()
    {
        var info = new UpdateInfo(
            new Version(1, 1, 2), "v1.1.2", "https://github.com/x", 1, "ABC", "https://github.com/p",
            new string('あ', StagedUpdate.MaxNotesLength + 10));

        var staged = StagedUpdate.From(info, "/f");

        Assert.Equal("v1.1.2", staged.Tag);
        Assert.Equal("ABC", staged.Sha256);
        Assert.Equal(StagedUpdate.MaxNotesLength, staged.ReleaseNotes.Length);
    }

    [Fact]
    public void ハッシュが無い版の控えはハッシュが空になる()
    {
        var info = new UpdateInfo(new Version(1, 1, 2), "v1.1.2", "https://github.com/x", 1, null, "", "");

        Assert.Equal(string.Empty, StagedUpdate.From(info, "/f").Sha256);
    }

    // ------------------------------------------------------------------
    // 設定への保存
    // ------------------------------------------------------------------

    [Fact]
    public void 自動更新は既定でオンで_切った設定は次の起動でも残る()
    {
        using var test = TestWorkspace.Create();

        var settings = new AppSettings(test.Workspace.Settings);
        Assert.True(settings.AutoUpdate);

        settings.AutoUpdate = false;

        Assert.False(new AppSettings(test.Workspace.Settings).AutoUpdate);
    }

    [Fact]
    public void 入れ替え待ちは次の起動でも分かり_消せる()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);
        var changed = 0;
        settings.Changed += (_, _) => changed++;

        Assert.Null(settings.StagedUpdate);

        settings.StagedUpdate = Staged();

        Assert.Equal(Staged(), new AppSettings(test.Workspace.Settings).StagedUpdate);

        settings.StagedUpdate = null;

        Assert.Null(new AppSettings(test.Workspace.Settings).StagedUpdate);

        // 内部の控えなので、画面の設定の変更としては知らせない
        Assert.Equal(0, changed);
    }

    [Fact]
    public void 更新した知らせの控えと止めたタグも保存できる()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);

        settings.AppliedUpdate = Staged();
        settings.AutoUpdateBlockedTag = "v1.1.2";

        var next = new AppSettings(test.Workspace.Settings);
        Assert.Equal(Staged(), next.AppliedUpdate);
        Assert.Equal("v1.1.2", next.AutoUpdateBlockedTag);

        next.AppliedUpdate = null;
        next.AutoUpdateBlockedTag = string.Empty;

        var last = new AppSettings(test.Workspace.Settings);
        Assert.Null(last.AppliedUpdate);
        Assert.Equal(string.Empty, last.AutoUpdateBlockedTag);
    }
}
