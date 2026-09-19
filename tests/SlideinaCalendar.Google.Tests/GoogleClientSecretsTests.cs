using System.Text;
using SlideinaCalendar.Google.OAuth;

namespace SlideinaCalendar.Google.Tests;

/// <summary>
/// Google Cloud Console から落とした client_secret_*.json の読み取り。
/// <para>長い文字列を手で写させない。写し間違いは認可が通らない形でしか現れない。</para>
/// </summary>
public class GoogleClientSecretsTests
{
    private const string Downloaded = """
        {"installed":{
          "client_id":"123-abc.apps.googleusercontent.com",
          "project_id":"slideina-calendar",
          "auth_uri":"https://accounts.google.com/o/oauth2/auth",
          "token_uri":"https://oauth2.googleapis.com/token",
          "client_secret":"GOCSPX-xxxx",
          "redirect_uris":["http://localhost"]}}
        """;

    private static Stream Json(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void 落としたファイルをそのまま読める()
    {
        using var stream = Json(Downloaded);
        var options = GoogleClientSecrets.Read(stream);

        Assert.Equal("123-abc.apps.googleusercontent.com", options.ClientId);
        Assert.Equal("GOCSPX-xxxx", options.ClientSecret);

        // スコープは要件書 6.1 のまま
        Assert.Equal(GoogleOAuthOptions.DefaultScopes, options.Scopes);
    }

    [Fact]
    public void 入れ子になっていない形でも読める()
    {
        using var stream = Json("""{"client_id":"id","client_secret":"secret"}""");
        var options = GoogleClientSecrets.Read(stream);

        Assert.Equal("id", options.ClientId);
    }

    [Fact]
    public void ウェブアプリ用は作り直しを促す()
    {
        // ウェブ型はループバックで受けられない。気づかないまま進むと認可で失敗する
        using var stream = Json("""{"web":{"client_id":"id","client_secret":"secret"}}""");

        var error = Assert.Throws<FormatException>(() => GoogleClientSecrets.Read(stream));

        Assert.Contains("デスクトップアプリ", error.Message);
    }

    [Fact]
    public void 足りない項目は名指しで知らせる()
    {
        using var stream = Json("""{"installed":{"client_id":"id"}}""");

        Assert.Contains("client_secret", Assert.Throws<FormatException>(
            () => GoogleClientSecrets.Read(stream)).Message);
    }

    [Fact]
    public void JSONでなければその旨を知らせる()
    {
        using var stream = Json("これはJSONではありません");

        Assert.Throws<FormatException>(() => GoogleClientSecrets.Read(stream));
    }

    // ------------------------------------------------------------------
    // 置き場所
    // ------------------------------------------------------------------

    [Fact]
    public void 取り込むと次からは読み込まずに使える()
    {
        using var directory = new TempDirectory();

        var source = Path.Combine(directory.Path, "client_secret_123.json");
        File.WriteAllText(source, Downloaded);

        var store = new GoogleClientSecretsStore(Path.Combine(directory.Path, "store", "google-client.json"));
        Assert.False(store.HasOwnFile);

        var imported = store.Import(source);

        Assert.Equal("123-abc.apps.googleusercontent.com", imported.ClientId);
        Assert.True(store.HasOwnFile);

        // 自分で入れたものは、焼き込んである既定より優先される
        Assert.Equal("123-abc.apps.googleusercontent.com", store.Load()!.ClientId);
    }

    [Fact]
    public void 壊れたファイルは置かない()
    {
        using var directory = new TempDirectory();

        var source = Path.Combine(directory.Path, "壊れている.json");
        File.WriteAllText(source, "{}");

        var store = new GoogleClientSecretsStore(Path.Combine(directory.Path, "google-client.json"));

        Assert.Throws<FormatException>(() => store.Import(source));

        // 置いてしまうと、次に開いたときに失敗する
        Assert.False(store.HasOwnFile);
    }

    [Fact]
    public void 入れていなければ焼き込んである既定になる()
    {
        using var directory = new TempDirectory();

        var store = new GoogleClientSecretsStore(Path.Combine(directory.Path, "無い.json"));

        Assert.False(store.HasOwnFile);

        // 焼き込んで組み立てていれば、利用者が何も入れなくても繋げる。
        // 焼き込まずに組み立てていれば、これまでどおり入れてもらう必要がある
        Assert.Equal(EmbeddedClientSettings.Options, store.Load());
    }

    [Fact]
    public void 消せる()
    {
        using var directory = new TempDirectory();

        var source = Path.Combine(directory.Path, "client.json");
        File.WriteAllText(source, Downloaded);

        var store = new GoogleClientSecretsStore(Path.Combine(directory.Path, "google-client.json"));
        store.Import(source);
        store.Clear();

        // 消えるのは自分で入れたぶんだけ。焼き込んである既定は残る
        Assert.False(store.HasOwnFile);
        Assert.Equal(EmbeddedClientSettings.Exists, store.Exists);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("slideina-test-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
