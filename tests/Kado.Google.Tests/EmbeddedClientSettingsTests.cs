using Kado.Google.OAuth;

namespace Kado.Google.Tests;

/// <summary>
/// アプリに焼き込む既定のクライアント設定。
/// <para>
/// 焼き込んであるかどうかは組み立て方で決まるので、ここでは<b>どちらでも筋が通ること</b>
/// を見る。値そのものは試験で固定できない。
/// </para>
/// </summary>
public class EmbeddedClientSettingsTests
{
    [Fact]
    public void 焼き込みの有無と中身が食い違わない()
    {
        if (EmbeddedClientSettings.Exists)
        {
            var options = Assert.IsType<GoogleOAuthOptions>(EmbeddedClientSettings.Options);

            // 半端な値で繋ぎにいくと、認可が通らない形でしか失敗が現れない
            Assert.False(string.IsNullOrWhiteSpace(options.ClientId));
            Assert.False(string.IsNullOrWhiteSpace(options.ClientSecret));
        }
        else
        {
            Assert.Null(EmbeddedClientSettings.Options);
        }
    }

    [Fact]
    public void 何度読んでも同じものを返す()
    {
        // 起動のたびに組み立て直さない。属性を舐めるのは一度でよい
        Assert.Same(EmbeddedClientSettings.Options, EmbeddedClientSettings.Options);
    }

    [Fact]
    public void 焼き込みがあれば既定のスコープが付く()
    {
        if (EmbeddedClientSettings.Options is not { } options) return;

        Assert.Equal(GoogleOAuthOptions.DefaultScopes, options.Scopes);
    }
}
