using Kado.Google.OAuth;

namespace Kado.Google.Tests;

/// <summary>
/// クライアント設定の置き場所。
/// <para>
/// ふつうは焼き込んである既定で足りる。自分のプロジェクトで使いたい人だけが
/// ファイルを置いて差し替える。
/// </para>
/// </summary>
public class GoogleClientSecretsStoreTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), $"slideina-{Guid.NewGuid():N}");

    private string StorePath => Path.Combine(_folder, "google-client.json");

    private GoogleClientSecretsStore Store => new(StorePath);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private string WriteSource(string id = "mine-123.apps.googleusercontent.com")
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "client_secret_test.json");

        File.WriteAllText(path, $$"""
            {
              "installed": {
                "client_id": "{{id}}",
                "client_secret": "mine-secret",
                "auth_uri": "https://accounts.google.com/o/oauth2/auth",
                "token_uri": "https://oauth2.googleapis.com/token"
              }
            }
            """);

        return path;
    }

    [Fact]
    public void 自分のファイルを置いていなければそう分かる()
    {
        Assert.False(Store.HasOwnFile);
    }

    [Fact]
    public void 焼き込みがあれば何も入れなくても繋げる()
    {
        // 利用者に Cloud Console を触らせないための肝
        Assert.Equal(EmbeddedClientSettings.Exists, Store.Exists);
    }

    [Fact]
    public void 自分のファイルを置けばそちらが使われる()
    {
        var store = Store;
        store.Import(WriteSource());

        Assert.True(store.HasOwnFile);
        Assert.True(store.Exists);

        var loaded = Assert.IsType<GoogleOAuthOptions>(store.Load());
        Assert.Equal("mine-123.apps.googleusercontent.com", loaded.ClientId);
    }

    [Fact]
    public void 自分のファイルを消しても繋げなくなりはしない()
    {
        var store = Store;
        store.Import(WriteSource());
        store.Clear();

        Assert.False(store.HasOwnFile);

        // 焼き込んである既定に戻るだけ
        Assert.Equal(EmbeddedClientSettings.Exists, store.Exists);
    }

    [Fact]
    public void 壊れたファイルは置かせない()
    {
        Directory.CreateDirectory(_folder);
        var broken = Path.Combine(_folder, "broken.json");
        File.WriteAllText(broken, "{ これは JSON ではない");

        var store = Store;
        Assert.Throws<FormatException>(() => store.Import(broken));

        // 置く前に弾く。壊れたものを置くと、次に開いたときに失敗する
        Assert.False(store.HasOwnFile);
    }
}
