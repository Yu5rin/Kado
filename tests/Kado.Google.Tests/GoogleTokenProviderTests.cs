using System.Net;
using System.Web;
using Kado.Google.OAuth;

namespace Kado.Google.Tests;

/// <summary>アクセストークンの配り方。失効が近ければ黙って取り直す。</summary>
public class GoogleTokenProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);

    private static GoogleOAuthOptions Options() => new()
    {
        ClientId = "id",
        ClientSecret = "secret",
    };

    private static OAuthTokens Tokens(string access, string? refresh, TimeSpan validFor) => new()
    {
        AccessToken = access,
        RefreshToken = refresh,
        ExpiresAt = Now + validFor,
    };

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task 余裕があれば取り直さない()
    {
        using var handler = new StubHttpHandler(_ => (HttpStatusCode.OK, "{}"));
        using var http = new HttpClient(handler);

        var store = new InMemoryTokenStore(Tokens("at-1", "rt-1", TimeSpan.FromHours(1)));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), store, new FixedTime(Now));

        Assert.Equal("at-1", await provider.GetAccessTokenAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 失効が近ければ取り直す()
    {
        using var handler = new StubHttpHandler(
            _ => (HttpStatusCode.OK, """{"access_token":"at-2","expires_in":3600}"""));
        using var http = new HttpClient(handler);

        // 1分後に切れる。呼び出しの最中に切れないよう手前で取り直す
        var store = new InMemoryTokenStore(Tokens("at-1", "rt-1", TimeSpan.FromMinutes(1)));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), store, new FixedTime(Now));

        Assert.Equal("at-2", await provider.GetAccessTokenAsync());

        var sent = HttpUtility.ParseQueryString(Assert.Single(handler.Requests));
        Assert.Equal("refresh_token", sent["grant_type"]);
        Assert.Equal("rt-1", sent["refresh_token"]);
    }

    [Fact]
    public async Task 取り直しても更新トークンは失わない()
    {
        // Google は更新トークンを返さないことがある。上書きすると次から繋げない
        using var handler = new StubHttpHandler(
            _ => (HttpStatusCode.OK, """{"access_token":"at-2","expires_in":3600}"""));
        using var http = new HttpClient(handler);

        var store = new InMemoryTokenStore(Tokens("at-1", "rt-1", TimeSpan.Zero));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), store, new FixedTime(Now));

        await provider.GetAccessTokenAsync();

        Assert.Equal("rt-1", store.Load()!.RefreshToken);
    }

    [Fact]
    public async Task 同時に呼ばれても取り直しは1回()
    {
        using var handler = new StubHttpHandler(
            _ => (HttpStatusCode.OK, """{"access_token":"at-2","expires_in":3600}"""));
        using var http = new HttpClient(handler);

        var store = new InMemoryTokenStore(Tokens("at-1", "rt-1", TimeSpan.Zero));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), store, new FixedTime(Now));

        // 予定とタスクは独立した経路なので並行して呼ばれる（要件書 6.3）
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => provider.GetAccessTokenAsync()));

        Assert.All(results, token => Assert.Equal("at-2", token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 繋いでいなければその旨を知らせる()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => (HttpStatusCode.OK, "{}")));

        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), new InMemoryTokenStore(),
            new FixedTime(Now));

        Assert.False(provider.IsConnected);

        var error = await Assert.ThrowsAsync<OAuthException>(() => provider.GetAccessTokenAsync());
        Assert.Contains("接続していません", error.Message);
    }

    [Fact]
    public async Task 更新トークンが無ければ繋ぎ直させる()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => (HttpStatusCode.OK, "{}")));

        var store = new InMemoryTokenStore(Tokens("at-1", refresh: null, TimeSpan.Zero));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), store, new FixedTime(Now));

        var error = await Assert.ThrowsAsync<OAuthException>(() => provider.GetAccessTokenAsync());
        Assert.Contains("接続し直して", error.Message);
    }

    [Fact]
    public async Task 接続を切ると控えも消す()
    {
        using var handler = new StubHttpHandler(_ => (HttpStatusCode.OK, "{}"));
        using var http = new HttpClient(handler);

        var store = new InMemoryTokenStore(Tokens("at-1", "rt-1", TimeSpan.FromHours(1)));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), store, new FixedTime(Now));

        await provider.DisconnectAsync();

        Assert.Null(store.Load());
        Assert.False(provider.IsConnected);
    }

    [Fact]
    public async Task 取り消しに失敗しても控えは消す()
    {
        // 残すと、繋がっているように見えて何も動かない
        using var handler = new StubHttpHandler(_ => (HttpStatusCode.BadRequest, """{"error":"invalid_token"}"""));
        using var http = new HttpClient(handler);

        var store = new InMemoryTokenStore(Tokens("at-1", "rt-1", TimeSpan.FromHours(1)));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), store, new FixedTime(Now));

        await provider.DisconnectAsync();

        Assert.Null(store.Load());
    }

    [Fact]
    public async Task 更新トークンが返らない認可は受け付けない()
    {
        // これを保存すると、開くたびにブラウザが立ち上がる
        using var handler = new StubHttpHandler(
            _ => (HttpStatusCode.OK, """{"access_token":"at-1","expires_in":3600}"""));
        using var http = new HttpClient(handler);

        var store = new InMemoryTokenStore();
        var flow = new LoopbackOAuthFlow(Options(), http, url => _ = Respond(url), new FixedTime(Now));
        using var provider = new GoogleTokenProvider(flow, store, new FixedTime(Now));

        await Assert.ThrowsAsync<OAuthException>(() => provider.ConnectAsync());
        Assert.Null(store.Load());
    }

    private static async Task Respond(string authorizationUrl)
    {
        var query = HttpUtility.ParseQueryString(new Uri(authorizationUrl).Query);

        using var client = new HttpClient();
        try
        {
            using var _ = await client
                .GetAsync($"{query["redirect_uri"]}?code=c1&state={query["state"]}").ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // 受け側が先に閉じることがある
        }
    }

    [Fact]
    public async Task 更新トークンが死んでいたら繋ぎ直させる()
    {
        // 同意画面が「テスト」のままなら7日で失効する。利用者が許可を
        // 取り消したときも同じ
        using var handler = new StubHttpHandler(
            _ => (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        using var http = new HttpClient(handler);

        var store = new InMemoryTokenStore(Tokens("at-1", "rt-1", TimeSpan.Zero));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), store, new FixedTime(Now));

        var error = await Assert.ThrowsAsync<OAuthException>(() => provider.GetAccessTokenAsync());

        Assert.Contains("接続し直して", error.Message);

        // 控えを残すと、繋がって見えるのに何をしても失敗し続ける
        Assert.Null(store.Load());
        Assert.False(provider.IsConnected);
    }

    [Fact]
    public async Task 一時的な失敗では控えを消さない()
    {
        // 通信が落ちただけで繋ぎ直しを求めるのは行き過ぎ
        using var handler = new StubHttpHandler(
            _ => (HttpStatusCode.ServiceUnavailable, """{"error":"backend_error"}"""));
        using var http = new HttpClient(handler);

        var store = new InMemoryTokenStore(Tokens("at-1", "rt-1", TimeSpan.Zero));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), store, new FixedTime(Now));

        await Assert.ThrowsAsync<OAuthException>(() => provider.GetAccessTokenAsync());

        Assert.NotNull(store.Load());
    }
}
