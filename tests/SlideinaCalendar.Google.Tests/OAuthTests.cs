using System.Net;
using System.Web;
using SlideinaCalendar.Google.OAuth;

namespace SlideinaCalendar.Google.Tests;

/// <summary>
/// ループバックリダイレクト方式の認可（要件書 6.2）。
/// <para>認可サーバは立てず、ブラウザの代わりに自分で戻り先を叩いて確かめる。</para>
/// </summary>
public class OAuthTests
{
    private static GoogleOAuthOptions Options() => new()
    {
        ClientId = "test-client.apps.googleusercontent.com",
        ClientSecret = "test-secret",
    };

    private const string TokenResponse = """
        {"access_token":"at-1","refresh_token":"rt-1","expires_in":3600,
         "scope":"https://www.googleapis.com/auth/tasks","token_type":"Bearer"}
        """;

    // ------------------------------------------------------------------
    // PKCE
    // ------------------------------------------------------------------

    [Fact]
    public void 検証子は毎回変わる()
    {
        var a = PkceCodes.Create();
        var b = PkceCodes.Create();

        Assert.NotEqual(a.Verifier, b.Verifier);
        Assert.NotEqual(a.Challenge, b.Challenge);
    }

    [Fact]
    public void 検証子はRFCの長さに収まる()
    {
        var codes = PkceCodes.Create();

        // RFC 7636 は 43〜128 文字
        Assert.InRange(codes.Verifier.Length, 43, 128);

        // URL に載せるので記号を含まない
        Assert.DoesNotContain('+', codes.Verifier);
        Assert.DoesNotContain('/', codes.Verifier);
        Assert.DoesNotContain('=', codes.Verifier);
    }

    // ------------------------------------------------------------------
    // 認可画面の URL
    // ------------------------------------------------------------------

    [Fact]
    public void 認可URLに必要な指定がそろっている()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => (HttpStatusCode.OK, "{}")));
        var flow = new LoopbackOAuthFlow(Options(), http, _ => { });

        var url = flow.BuildAuthorizationUrl("http://127.0.0.1:1234/", "challenge-1", "state-1");
        var query = HttpUtility.ParseQueryString(new Uri(url).Query);

        Assert.Equal("test-client.apps.googleusercontent.com", query["client_id"]);
        Assert.Equal("http://127.0.0.1:1234/", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("challenge-1", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("state-1", query["state"]);

        // この2つが無いと更新トークンが返らず、開くたびにブラウザが立ち上がる
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);
    }

    [Fact]
    public void 要求するスコープは要件どおり()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => (HttpStatusCode.OK, "{}")));
        var flow = new LoopbackOAuthFlow(Options(), http, _ => { });

        var query = HttpUtility.ParseQueryString(
            new Uri(flow.BuildAuthorizationUrl("http://127.0.0.1:1/", "c", "s")).Query);

        Assert.Equal(
            "https://www.googleapis.com/auth/calendar.events "
            + "https://www.googleapis.com/auth/calendar.readonly "
            + "https://www.googleapis.com/auth/calendar.app.created "
            + "https://www.googleapis.com/auth/tasks",
            query["scope"]);
    }

    // ------------------------------------------------------------------
    // 通しの流れ
    // ------------------------------------------------------------------

    [Fact]
    public async Task ブラウザが戻ってくるとトークンを受け取る()
    {
        using var handler = new StubHttpHandler(_ => (HttpStatusCode.OK, TokenResponse));
        using var http = new HttpClient(handler);

        var flow = new LoopbackOAuthFlow(Options(), http, url => _ = Visit(url, withCode: "code-1"));
        var tokens = await flow.AuthorizeAsync();

        Assert.Equal("at-1", tokens.AccessToken);
        Assert.Equal("rt-1", tokens.RefreshToken);

        // 検証子を送らないと、コードを横取りされたときに交換されてしまう
        var sent = HttpUtility.ParseQueryString(Assert.Single(handler.Requests));
        Assert.Equal("code-1", sent["code"]);
        Assert.Equal("authorization_code", sent["grant_type"]);
        Assert.False(string.IsNullOrEmpty(sent["code_verifier"]));
    }

    [Fact]
    public async Task 断られたらその旨がわかる()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => (HttpStatusCode.OK, TokenResponse)));
        var flow = new LoopbackOAuthFlow(Options(), http, url => _ = Visit(url, error: "access_denied"));

        var error = await Assert.ThrowsAsync<OAuthException>(() => flow.AuthorizeAsync());

        Assert.True(error.WasDeclined);
    }

    [Fact]
    public async Task stateが合わなければ受け付けない()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => (HttpStatusCode.OK, TokenResponse)));

        // こちらが始めた認可ではない応答
        var flow = new LoopbackOAuthFlow(Options(), http, url => _ = Visit(url, "code-1", state: "よその state"));

        var error = await Assert.ThrowsAsync<OAuthException>(() => flow.AuthorizeAsync());

        Assert.Contains("一致しませんでした", error.Message);
    }

    [Fact]
    public async Task 交換に失敗したら中身を見せずに知らせる()
    {
        using var http = new HttpClient(new StubHttpHandler(
            _ => (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")));

        var flow = new LoopbackOAuthFlow(Options(), http, url => _ = Visit(url, withCode: "code-1"));

        var error = await Assert.ThrowsAsync<OAuthException>(() => flow.AuthorizeAsync());

        Assert.Equal("invalid_grant", error.Error);
    }

    // ------------------------------------------------------------------
    // 打ち切り
    // ------------------------------------------------------------------

    [Fact]
    public async Task 取り消すと待ちを抜ける()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => (HttpStatusCode.OK, TokenResponse)));

        // ブラウザは開かせるだけで、戻り先は叩かない（許可待ちのまま）
        var flow = new LoopbackOAuthFlow(Options(), http, _ => { });

        using var cts = new CancellationTokenSource();
        var task = flow.AuthorizeAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task 戻ってこなければ諦める()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => (HttpStatusCode.OK, TokenResponse)));

        // 上限を極端に短くして、5分待たずに確かめる
        var flow = new LoopbackOAuthFlow(
            Options(), http, _ => { }, authorizationTimeout: TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<OAuthException>(() => flow.AuthorizeAsync());

        Assert.Contains("もう一度お試しください", error.Message);
    }

    // ------------------------------------------------------------------
    // 応答の読み取り
    // ------------------------------------------------------------------

    [Fact]
    public void 失効時刻は受け取った秒数から決まる()
    {
        var now = new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
        var tokens = LoopbackOAuthFlow.Parse(TokenResponse, now);

        Assert.Equal(now.AddHours(1), tokens.ExpiresAt);
        Assert.Equal(["https://www.googleapis.com/auth/tasks"], tokens.Scopes);
    }

    [Fact]
    public void アクセストークンが無ければ弾く()
    {
        Assert.Throws<OAuthException>(() => LoopbackOAuthFlow.Parse("""{"expires_in":3600}"""));
    }

    // ------------------------------------------------------------------
    // 補助
    // ------------------------------------------------------------------

    /// <summary>ブラウザの代わりに戻り先を叩く。</summary>
    private static async Task Visit(string authorizationUrl, string? withCode = null, string? error = null,
        string? state = null)
    {
        var query = HttpUtility.ParseQueryString(new Uri(authorizationUrl).Query);
        var redirect = query["redirect_uri"]!;

        var response = HttpUtility.ParseQueryString(string.Empty);
        response["state"] = state ?? query["state"];
        if (withCode is not null) response["code"] = withCode;
        if (error is not null) response["error"] = error;

        using var client = new HttpClient();
        try
        {
            using var _ = await client.GetAsync($"{redirect}?{response}").ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // 受け側が先に閉じることがある。認可の判定はそちらで行う
        }
    }
}
