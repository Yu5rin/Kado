using System.Net;
using System.Net.Http.Headers;
using Kado.Core.Net;
using Kado.Google.OAuth;

namespace Kado.Google.Tests;

/// <summary>
/// 401 のときのトークンの取り直し、トークンの取得先が返したプロキシの断り、認可の受け口を開けなかったとき。
/// <para>
/// どれも会社の回線・セキュリティソフトの下で起きる。画面の文言と shell.log が原因を言えるように、
/// 見分けられる形で例外にして、記録に1行残す。
/// </para>
/// </summary>
public class GoogleNetworkFailureTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);

    private static GoogleOAuthOptions Options() => new() { ClientId = "id", ClientSecret = "secret" };

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

    // ------------------------------------------------------------------
    // 401 でのトークンの取り直し
    // ------------------------------------------------------------------

    [Fact]
    public async Task 断られたトークンなら期限が残っていても取り直す()
    {
        using var handler = new StubHttpHandler(
            _ => (HttpStatusCode.OK, """{"access_token":"at-2","expires_in":3600}"""));
        using var http = new HttpClient(handler);

        // 期限は1時間残っている。それでも Google が 401 で断った（時計のずれ・Google 側での失効）
        var store = new InMemoryTokenStore(Tokens("at-1", "rt-1", TimeSpan.FromHours(1)));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), store, new FixedTime(Now));

        var fresh = await provider.RefreshAccessTokenAsync("at-1");

        Assert.Equal("at-2", fresh);
        Assert.Equal("at-2", store.Load()!.AccessToken);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task すでに別の呼び出しが取り直していれば取り直さない()
    {
        using var handler = new StubHttpHandler(
            _ => (HttpStatusCode.OK, """{"access_token":"at-3","expires_in":3600}"""));
        using var http = new HttpClient(handler);

        // 控えのトークンは、断られたもの（at-1）と違う。同時に断られた別の呼び出しが先に取り直した
        var store = new InMemoryTokenStore(Tokens("at-2", "rt-1", TimeSpan.FromHours(1)));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), store, new FixedTime(Now));

        var fresh = await provider.RefreshAccessTokenAsync("at-1");

        Assert.Equal("at-2", fresh);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 同時に断られても取り直しは1回()
    {
        using var handler = new StubHttpHandler(
            _ => (HttpStatusCode.OK, """{"access_token":"at-2","expires_in":3600}"""));
        using var http = new HttpClient(handler);

        var store = new InMemoryTokenStore(Tokens("at-1", "rt-1", TimeSpan.FromHours(1)));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), store, new FixedTime(Now));

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => provider.RefreshAccessTokenAsync("at-1")));

        Assert.All(results, token => Assert.Equal("at-2", token));
        Assert.Single(handler.Requests);
    }

    // ------------------------------------------------------------------
    // トークンの取得先が返した、Google の言葉ではない応答
    // ------------------------------------------------------------------

    [Fact]
    public async Task トークンの取得でプロキシに断られたら状態コードを持った例外にする()
    {
        var lines = new List<string>();
        using var handler = new StubHttpHandler(_ => (HttpStatusCode.ProxyAuthenticationRequired, "<html>認証</html>"));
        using var http = new HttpClient(handler);

        var store = new InMemoryTokenStore(Tokens("at-1", "rt-1", TimeSpan.Zero));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now), log: lines.Add),
            store, new FixedTime(Now));

        // Google の言葉（error）が無い。OAuthException にせず、状態コードを持った通信の例外にして、
        // 画面が「プロキシの認証」と言い分けられるようにする
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetAccessTokenAsync());
        Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, error.StatusCode);

        // 記録には、応答の要点（本文ではない）が残る
        var line = Assert.Single(lines);
        Assert.Contains("トークンの取り直し", line, StringComparison.Ordinal);
        Assert.Contains("407", line, StringComparison.Ordinal);
        Assert.DoesNotContain("認証</html>", line, StringComparison.Ordinal);

        // 通信が転んだだけで、繋ぎ直しは求めない
        Assert.NotNull(store.Load());
    }

    [Fact]
    public async Task Googleが理由を付けて断れば従来どおりOAuthException()
    {
        using var handler = new StubHttpHandler(
            _ => (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        using var http = new HttpClient(handler);

        var store = new InMemoryTokenStore(Tokens("at-1", "rt-1", TimeSpan.Zero));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now)), store, new FixedTime(Now));

        var error = await Assert.ThrowsAsync<OAuthException>(() => provider.GetAccessTokenAsync());

        Assert.True(error.Message.Contains("接続し直して", StringComparison.Ordinal));
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => throw error;
    }

    [Fact]
    public async Task トークンの取得で通信が転んだら例外の連鎖を記録に残す()
    {
        var lines = new List<string>();
        using var http = new HttpClient(new ThrowingHandler(new HttpRequestException(
            "SSL 接続を確立できませんでした", new System.Security.Authentication.AuthenticationException("リモート証明書が無効"))));

        var store = new InMemoryTokenStore(Tokens("at-1", "rt-1", TimeSpan.Zero));
        using var provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now), log: lines.Add),
            store, new FixedTime(Now));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetAccessTokenAsync());

        var line = Assert.Single(lines);
        Assert.Contains("トークンの取り直し: 通信に失敗", line, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException", line, StringComparison.Ordinal);
        Assert.Contains("AuthenticationException", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取り消しが届かなかったことも記録に残す()
    {
        var lines = new List<string>();
        using var handler = new StubHttpHandler(_ => (HttpStatusCode.ProxyAuthenticationRequired, "x"));
        using var http = new HttpClient(handler);

        var flow = new LoopbackOAuthFlow(Options(), http, _ => { }, new FixedTime(Now), log: lines.Add);

        await flow.RevokeAsync("rt-1");

        Assert.Contains(lines, l => l.Contains("取り消し", StringComparison.Ordinal) && l.Contains("407", StringComparison.Ordinal));

        // すでに無効なトークンの 400 は、記録するほどのことではない
        lines.Clear();
        using var already = new HttpClient(new StubHttpHandler(_ => (HttpStatusCode.BadRequest, """{"error":"invalid_token"}""")));
        await new LoopbackOAuthFlow(Options(), already, _ => { }, new FixedTime(Now), log: lines.Add).RevokeAsync("rt-1");
        Assert.Empty(lines);
    }

    // ------------------------------------------------------------------
    // 認可の受け口（HttpListener）を開けなかったとき
    // ------------------------------------------------------------------

    [Fact]
    public void 受け口を開けなかったら理由つきの例外にして記録に残す()
    {
        var lines = new List<string>();

        // 5 = Access denied（URL の予約が無い・セキュリティソフトに止められた）
        var error = LoopbackOAuthFlow.ToOAuthException(new HttpListenerException(5), lines.Add);

        Assert.Contains("準備ができませんでした", error.Message, StringComparison.Ordinal);
        Assert.Contains("権限が足りません", error.Message, StringComparison.Ordinal);
        Assert.IsType<HttpListenerException>(error.InnerException);

        var line = Assert.Single(lines);
        Assert.Contains("エラー 5", line, StringComparison.Ordinal);
        Assert.Contains("HttpListenerException", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5, "権限が足りません")]
    [InlineData(32, "別のアプリが使っています")]
    [InlineData(183, "別のアプリが使っています")]
    [InlineData(87, "エラー 87")]
    public void 受け口を開けなかった理由を番号から言葉にする(int code, string expected) =>
        Assert.Contains(expected, LoopbackOAuthFlow.DescribeListenerFailure(new HttpListenerException(code)), StringComparison.Ordinal);

    // ------------------------------------------------------------------
    // 認可の待ちを中止できる
    // ------------------------------------------------------------------

    [Fact]
    public async Task 認可の待ちは中止で止まり失敗の文言にならない()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => (HttpStatusCode.OK, "{}")));
        var flow = new LoopbackOAuthFlow(Options(), http, _ => { /* ブラウザは開かない。誰も戻ってこない */ });

        using var cts = new CancellationTokenSource();
        var authorizing = flow.AuthorizeAsync(cts.Token);

        await Task.Delay(100);
        cts.Cancel();

        // 5分待たずに、利用者が止めたこととして抜ける（時間切れの OAuthException ではない）
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorizing);
    }
}
