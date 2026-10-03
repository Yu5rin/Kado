using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Kado.Core.Net;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// Google の API が 429（呼びすぎ）・5xx・401 を返したときの、待って出し直す動き。
/// <para>
/// 以前は一度断られたらそのまま失敗にして、1件ずつ警告を並べながら残りも叩き続けていた。
/// 待つ長さは実時間ではなく、記録だけする待ち方に差し替えて確かめる。
/// </para>
/// </summary>
public class GoogleRetryTests
{
    /// <summary>送られた要求の控え。出し直しで本文とトークンが正しく付き直るかを見る。</summary>
    private sealed record Seen(HttpMethod Method, string Url, string? Authorization, string Body);

    /// <summary>呼ばれた順に、決めた応答を返す。足りなくなったら最後のものを繰り返す。</summary>
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)));

            var index = Math.Min(Requests.Count - 1, responses.Length - 1);
            return responses[index]();
        }
    }

    private static Func<HttpResponseMessage> Respond(
        HttpStatusCode status, string body = "{}", Action<HttpResponseMessage>? configure = null) =>
        () =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            configure?.Invoke(response);
            return response;
        };

    private static Func<HttpResponseMessage> RetryAfter(HttpStatusCode status, int seconds) =>
        Respond(status, "{}", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)));

    /// <summary>トークンを出す。取り直すと番号が進む。</summary>
    private sealed class RotatingToken : IAccessTokenSource
    {
        private int _generation = 1;

        public int Refreshes { get; private set; }

        /// <summary>true なら、取り直しても同じトークンを返す（取り直せない実装）。</summary>
        public bool CannotRefresh { get; set; }

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult($"token-{_generation}");

        public Task<string> RefreshAccessTokenAsync(string rejectedToken, CancellationToken cancellationToken = default)
        {
            if (!CannotRefresh && rejectedToken == $"token-{_generation}")
            {
                _generation++;
                Refreshes++;
            }

            return Task.FromResult($"token-{_generation}");
        }
    }

    private static (GoogleCalendarApi Api, ScriptedHandler Handler, List<TimeSpan> Waits, RotatingToken Token) Create(
        params Func<HttpResponseMessage>[] responses)
    {
        var handler = new ScriptedHandler(responses);
        var waits = new List<TimeSpan>();
        var token = new RotatingToken();

        var retry = new GoogleRetryPolicy((span, _) =>
        {
            waits.Add(span);
            return Task.CompletedTask;
        });

        return (new GoogleCalendarApi(new HttpClient(handler), token, retry), handler, waits, token);
    }

    private const string EmptyList = """{"items":[]}""";

    // ------------------------------------------------------------------
    // 429・5xx
    // ------------------------------------------------------------------

    [Fact]
    public async Task 呼びすぎはRetryAfterの長さだけ待って出し直す()
    {
        var (api, handler, waits, _) = Create(RetryAfter(HttpStatusCode.TooManyRequests, 3), Respond(HttpStatusCode.OK, EmptyList));

        var page = await api.ListEventsAsync("primary");

        Assert.Empty(page.Items);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(3)], waits);
    }

    [Fact]
    public async Task RetryAfterが無ければ倍にしながら待つ()
    {
        var unavailable = Respond(HttpStatusCode.ServiceUnavailable);
        var (api, handler, waits, _) = Create(unavailable, unavailable, unavailable, Respond(HttpStatusCode.OK, EmptyList));

        await api.ListEventsAsync("primary");

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], waits);
    }

    [Fact]
    public async Task 出し直しは数回だけで諦めて例外にする()
    {
        var (api, handler, waits, _) = Create(Respond(HttpStatusCode.ServiceUnavailable));

        var error = await Assert.ThrowsAsync<GoogleApiException>(() => api.ListEventsAsync("primary"));

        // 最初の1回と、出し直し3回
        Assert.Equal(1 + GoogleRetryPolicy.MaxRetries, handler.Requests.Count);
        Assert.Equal(GoogleRetryPolicy.MaxRetries, waits.Count);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task 長すぎるRetryAfterは待たずに諦める()
    {
        // 画面を何分も固めない。次回の同期に回す
        var (api, handler, waits, _) = Create(RetryAfter(HttpStatusCode.TooManyRequests, 120));

        var error = await Assert.ThrowsAsync<GoogleApiException>(() => api.ListEventsAsync("primary"));

        Assert.Single(handler.Requests);
        Assert.Empty(waits);
        Assert.True(error.IsRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(120), error.RetryAfter);
    }

    [Fact]
    public async Task 一度の同期で待つ合計にも上限がある()
    {
        // 30秒ずつ言われると、2回（合計60秒）で使い切る
        var (api, handler, waits, _) = Create(RetryAfter(HttpStatusCode.TooManyRequests, 30));

        await Assert.ThrowsAsync<GoogleApiException>(() => api.ListEventsAsync("primary"));

        Assert.Equal(2, waits.Count);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task 待った合計は数え直せる()
    {
        var handler = new ScriptedHandler(RetryAfter(HttpStatusCode.TooManyRequests, 30));
        var waits = new List<TimeSpan>();
        var retry = new GoogleRetryPolicy((span, _) => { waits.Add(span); return Task.CompletedTask; });
        var api = new GoogleCalendarApi(new HttpClient(handler), new RotatingToken(), retry);

        await Assert.ThrowsAsync<GoogleApiException>(() => api.ListEventsAsync("primary"));
        Assert.Equal(TimeSpan.FromSeconds(60), retry.Waited);

        // 次の同期の頭で数え直す
        retry.ResetBudget();
        Assert.Equal(TimeSpan.Zero, retry.Waited);

        waits.Clear();
        await Assert.ThrowsAsync<GoogleApiException>(() => api.ListEventsAsync("primary"));
        Assert.Equal(2, waits.Count);
    }

    [Fact]
    public async Task 作成のPOSTは500で出し直さない()
    {
        // 向こうが受け付けたのに応答だけ失われたとき、出し直すと二重に作ってしまう
        var (api, handler, waits, _) = Create(Respond(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<GoogleApiException>(
            () => api.InsertEventAsync("primary", new System.Text.Json.Nodes.JsonObject { ["summary"] = "定例" }));

        Assert.Single(handler.Requests);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task 作成のPOSTでも503と呼びすぎは出し直す()
    {
        // 503・429 は、向こうが受け付けていないと言える
        var (api, handler, _, _) = Create(
            Respond(HttpStatusCode.ServiceUnavailable),
            RetryAfter(HttpStatusCode.TooManyRequests, 1),
            Respond(HttpStatusCode.OK, """{"id":"g1"}"""));

        var created = await api.InsertEventAsync("primary", new System.Text.Json.Nodes.JsonObject { ["summary"] = "定例" });

        Assert.Equal("g1", created.GetProperty("id").GetString());
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task 書き換えのPATCHは500でも出し直す()
    {
        var (api, handler, _, _) = Create(Respond(HttpStatusCode.InternalServerError), Respond(HttpStatusCode.OK, """{"id":"g1"}"""));

        await api.PatchEventAsync("primary", "g1", new System.Text.Json.Nodes.JsonObject { ["summary"] = "変更" });

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task 出し直しても同じ本文を送る()
    {
        var (api, handler, _, _) = Create(Respond(HttpStatusCode.ServiceUnavailable), Respond(HttpStatusCode.OK, """{"id":"g1"}"""));

        await api.PatchEventAsync("primary", "g1", new System.Text.Json.Nodes.JsonObject { ["summary"] = "changed" });

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("changed", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
        Assert.Equal(HttpMethod.Patch, handler.Requests[1].Method);
    }

    [Fact]
    public async Task 呼びすぎの403は出し直し権限不足の403は出し直さない()
    {
        var (limited, limitedHandler, _, _) = Create(
            Respond(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"rateLimitExceeded"}]}}"""),
            Respond(HttpStatusCode.OK, EmptyList));

        await limited.ListEventsAsync("primary");
        Assert.Equal(2, limitedHandler.Requests.Count);

        var (denied, deniedHandler, waits, _) = Create(
            Respond(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"insufficientPermissions"}]}}"""));

        await Assert.ThrowsAsync<GoogleApiException>(() => denied.ListEventsAsync("primary"));
        Assert.Single(deniedHandler.Requests);
        Assert.Empty(waits);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task 待っても直らない断られ方は出し直さない(HttpStatusCode status)
    {
        var (api, handler, waits, _) = Create(Respond(status));

        await Assert.ThrowsAsync<GoogleApiException>(() => api.ListEventsAsync("primary"));

        Assert.Single(handler.Requests);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task 待っている間に中止できる()
    {
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable));

        // 本物の待ち方。中止が立てば、待ちきらずに抜ける
        var retry = new GoogleRetryPolicy();
        var api = new GoogleCalendarApi(new HttpClient(handler), new RotatingToken(), retry);

        var running = api.ListEventsAsync("primary", cancellationToken: cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task タスクとドライブのAPIも同じく出し直す()
    {
        var tasksHandler = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable), Respond(HttpStatusCode.OK, """{"items":[]}"""));
        var tasks = new GoogleTasksApi(
            new HttpClient(tasksHandler), new RotatingToken(), new GoogleRetryPolicy((_, _) => Task.CompletedTask));

        await tasks.ListTaskListsAsync();
        Assert.Equal(2, tasksHandler.Requests.Count);

        var driveHandler = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable), Respond(HttpStatusCode.OK, """{"id":"f1"}"""));
        var drive = new GoogleDriveApi(
            new HttpClient(driveHandler), new RotatingToken(), new GoogleRetryPolicy((_, _) => Task.CompletedTask));

        var folder = await drive.CreateFolderAsync("Kado");
        Assert.Equal("f1", folder.GetProperty("id").GetString());
        Assert.Equal(2, driveHandler.Requests.Count);
    }

    // ------------------------------------------------------------------
    // 401
    // ------------------------------------------------------------------

    [Fact]
    public async Task トークンが通らなければ一度だけ取り直して出し直す()
    {
        var (api, handler, _, token) = Create(Respond(HttpStatusCode.Unauthorized), Respond(HttpStatusCode.OK, EmptyList));

        await api.ListEventsAsync("primary");

        Assert.Equal(1, token.Refreshes);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer token-1", handler.Requests[0].Authorization);

        // 出し直しには、取り直した新しいトークンが付く
        Assert.Equal("Bearer token-2", handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task 取り直したあとも通らなければ取り直しを重ねず諦める()
    {
        var (api, handler, _, token) = Create(Respond(HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<GoogleApiException>(() => api.ListEventsAsync("primary"));

        Assert.True(error.IsUnauthorized);
        Assert.Equal(1, token.Refreshes);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task 取り直せない実装は同じトークンのまま出し直さない()
    {
        var (api, handler, _, token) = Create(Respond(HttpStatusCode.Unauthorized));
        token.CannotRefresh = true;

        await Assert.ThrowsAsync<GoogleApiException>(() => api.ListEventsAsync("primary"));

        Assert.Single(handler.Requests);
    }

    // ------------------------------------------------------------------
    // 407・記録に残す要点
    // ------------------------------------------------------------------

    [Fact]
    public async Task プロキシの認証は見分けられて応答の要点が残る()
    {
        var (api, _, waits, _) = Create(Respond(
            HttpStatusCode.ProxyAuthenticationRequired, "<html>認証が必要です</html>",
            r => r.Headers.ProxyAuthenticate.Add(new AuthenticationHeaderValue("NTLM"))));

        var error = await Assert.ThrowsAsync<GoogleApiException>(() => api.ListEventsAsync("primary"));

        Assert.True(error.IsProxyAuthRequired);
        Assert.False(error.IsTransient);
        Assert.Empty(waits);

        // shell.log の1行に、状態・Content-Type・認証方式が残る（本文は残さない）
        var line = NetworkDiagnostics.Summarize(error);
        Assert.Contains("407", line, StringComparison.Ordinal);
        Assert.Contains("Content-Type=application/json", line, StringComparison.Ordinal);
        Assert.Contains("Proxy-Authenticate=NTLM", line, StringComparison.Ordinal);
        Assert.DoesNotContain("認証が必要です", line, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryAfterは日時の指定も待つ長さにする()
    {
        var now = new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);

        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(7));

        Assert.Equal(TimeSpan.FromSeconds(7), GoogleHttp.RetryAfterOf(response, now));

        // 過ぎた日時なら、すぐ出し直してよい
        Assert.Equal(TimeSpan.Zero, GoogleHttp.RetryAfterOf(response, now.AddSeconds(10)));
    }

    // ------------------------------------------------------------------
    // 方針そのもの
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("GET", HttpStatusCode.TooManyRequests, true)]
    [InlineData("GET", HttpStatusCode.InternalServerError, true)]
    [InlineData("GET", HttpStatusCode.BadGateway, true)]
    [InlineData("GET", HttpStatusCode.ServiceUnavailable, true)]
    [InlineData("GET", HttpStatusCode.GatewayTimeout, true)]
    [InlineData("GET", HttpStatusCode.BadRequest, false)]
    [InlineData("GET", HttpStatusCode.NotFound, false)]
    [InlineData("POST", HttpStatusCode.TooManyRequests, true)]
    [InlineData("POST", HttpStatusCode.ServiceUnavailable, true)]
    [InlineData("POST", HttpStatusCode.InternalServerError, false)]
    [InlineData("POST", HttpStatusCode.BadGateway, false)]
    [InlineData("POST", HttpStatusCode.GatewayTimeout, false)]
    [InlineData("DELETE", HttpStatusCode.InternalServerError, true)]
    public void 出し直す場面を絞る(string method, HttpStatusCode status, bool expected) =>
        Assert.Equal(expected, GoogleRetryPolicy.ShouldRetry(new HttpMethod(method), status, isRateLimited403: false));

    [Fact]
    public void 出し直さない方針は待たない()
    {
        Assert.Null(GoogleRetryPolicy.None.NextDelay(
            HttpMethod.Get, HttpStatusCode.TooManyRequests, isRateLimited403: false, retryAfter: null, retriesSoFar: 0));
    }
}
