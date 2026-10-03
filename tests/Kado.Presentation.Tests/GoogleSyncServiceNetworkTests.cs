using System.Net;
using Kado.Google.Sync;
using Kado.Presentation.Net;
using Kado.Presentation.Sync;

namespace Kado.Presentation.Tests;

/// <summary>
/// 同期の流れの中での、通信の失敗の扱い。
/// <list type="bullet">
/// <item>呼びすぎ・Google の不調は、残りを次回に回して警告1行（呼びすぎのときだけ間隔を延ばす合図）</item>
/// <item>プロキシの認証（407）とトークンが通らない（401）は、どのカレンダーでも同じ。1つずつ警告にせず同期ごと失敗</item>
/// <item>警告には型名や英語のメッセージを出さず、詳細は shell.log に1行残す</item>
/// </list>
/// </summary>
public class GoogleSyncServiceNetworkTests : IDisposable
{
    private readonly TestWorkspace _test = TestWorkspace.Create();

    public void Dispose() => _test.Dispose();

    private sealed class RoutingHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());

            return Task.FromResult(respond(request.RequestUri.ToString()));
        }
    }

    private sealed class FixedToken : IAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("ya29.test");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private const string ThreeCalendars = """
        {"items":[
          {"id":"a@group.calendar.google.com","summary":"仕事","accessRole":"owner"},
          {"id":"b@group.calendar.google.com","summary":"家庭","accessRole":"owner"},
          {"id":"c@group.calendar.google.com","summary":"趣味","accessRole":"owner"}]}
        """;

    private GoogleSyncService Create(RoutingHandler handler, List<string>? lines = null)
    {
        var http = new HttpClient(handler);
        var token = new FixedToken();

        return new GoogleSyncService(
            _test.Workspace,
            new GoogleCalendarApi(http, token, GoogleRetryPolicy.None),
            new GoogleTasksApi(http, token, GoogleRetryPolicy.None),
            log: lines is null ? null : new NetworkLog(lines.Add));
    }

    private static HttpResponseMessage Route(string url, Func<string, HttpResponseMessage?> events) => url switch
    {
        _ when url.Contains("calendarList", StringComparison.Ordinal) => Json(HttpStatusCode.OK, ThreeCalendars),
        _ when url.Contains("/colors", StringComparison.Ordinal) => Json(HttpStatusCode.OK, """{"calendar":{}}"""),
        _ when url.Contains("tasks.googleapis", StringComparison.Ordinal) => Json(HttpStatusCode.OK, """{"items":[]}"""),
        _ => events(url) ?? Json(HttpStatusCode.OK, """{"items":[],"nextSyncToken":"t"}"""),
    };

    private static int EventCalls(RoutingHandler handler) => handler.Urls.Count(u => u.Contains("/events", StringComparison.Ordinal));

    // ------------------------------------------------------------------
    // 呼びすぎ・Google の不調
    // ------------------------------------------------------------------

    [Fact]
    public async Task 一覧が呼びすぎで断られたら残りを叩かず次回に回す()
    {
        var handler = new RoutingHandler(url => url.Contains("calendarList", StringComparison.Ordinal)
            ? Json(HttpStatusCode.TooManyRequests, """{"error":{"errors":[{"reason":"rateLimitExceeded"}]}}""")
            : Json(HttpStatusCode.OK, """{"items":[]}"""));
        using var service = Create(handler);

        var report = await service.SyncAsync();

        Assert.True(report!.Throttled);
        Assert.True(report.Deferred);
        Assert.Equal([SyncReport.BusyWarning], report.Warnings);

        // 一覧で止まる。タスクにも各カレンダーにも進まない
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task 一覧の不調は警告にして続けるが間隔を延ばす理由にはしない()
    {
        var handler = new RoutingHandler(url => url.Contains("calendarList", StringComparison.Ordinal)
            ? Json(HttpStatusCode.ServiceUnavailable, "{}")
            : Json(HttpStatusCode.OK, """{"items":[]}"""));
        using var service = Create(handler);

        var report = await service.SyncAsync();

        Assert.True(report!.Deferred);
        Assert.False(report.Throttled);
        Assert.Contains(SyncReport.BusyWarning, report.Warnings);
    }

    [Fact]
    public async Task カレンダー1つの呼びすぎで残りのカレンダーも次回に回す()
    {
        var handler = new RoutingHandler(url => Route(url, u => u.Contains("a%40group", StringComparison.Ordinal) || u.Contains("a@group", StringComparison.Ordinal)
            ? Json(HttpStatusCode.TooManyRequests, """{"error":{"errors":[{"reason":"rateLimitExceeded"}]}}""")
            : null));
        using var service = Create(handler);

        var report = await service.SyncAsync();

        Assert.True(report!.Throttled);
        Assert.Equal([SyncReport.BusyWarning], report.Warnings);

        // 最初のカレンダーで止まる。残りの2つには行かない
        Assert.Equal(1, EventCalls(handler));
    }

    [Fact]
    public async Task カレンダー1つの不調は警告1行で残りは続ける()
    {
        // 誕生日のような特殊なカレンダーが 5xx を返し続けても、他のカレンダーは同期する。間隔も延ばさない
        var handler = new RoutingHandler(url => Route(url, u => u.Contains("a%40group", StringComparison.Ordinal) || u.Contains("a@group", StringComparison.Ordinal)
            ? Json(HttpStatusCode.InternalServerError, "{}")
            : null));
        using var service = Create(handler);

        var report = await service.SyncAsync();

        Assert.True(report!.Deferred);
        Assert.False(report.Throttled);
        Assert.Equal([SyncReport.BusyWarning], report.Warnings);

        // 3つとも試した
        Assert.Equal(3, EventCalls(handler));
    }

    // ------------------------------------------------------------------
    // 407・401 は同期ごと失敗
    // ------------------------------------------------------------------

    [Fact]
    public async Task プロキシの認証は1つずつ警告にせず同期ごと失敗にする()
    {
        var handler = new RoutingHandler(url => url.Contains("calendarList", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, ThreeCalendars)
            : url.Contains("/colors", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"calendar":{}}""")
                : Json(HttpStatusCode.ProxyAuthenticationRequired, "<html></html>"));
        using var service = Create(handler);

        var error = await Assert.ThrowsAsync<GoogleApiException>(() => service.SyncAsync());

        Assert.True(error.IsProxyAuthRequired);

        // カレンダーの数だけ叩き続けない
        Assert.True(EventCalls(handler) <= 1);
    }

    [Fact]
    public async Task 一覧の段階の407も同期ごと失敗にする()
    {
        var handler = new RoutingHandler(_ => Json(HttpStatusCode.ProxyAuthenticationRequired, "<html></html>"));
        using var service = Create(handler);

        await Assert.ThrowsAsync<GoogleApiException>(() => service.SyncAsync());

        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task トークンが通らなければ同期ごと失敗にする()
    {
        var handler = new RoutingHandler(url => Route(url, _ => Json(HttpStatusCode.Unauthorized, """{"error":{"errors":[{"reason":"authError"}]}}""")));
        using var service = Create(handler);

        var error = await Assert.ThrowsAsync<GoogleApiException>(() => service.SyncAsync());

        Assert.True(error.IsUnauthorized);
    }

    // ------------------------------------------------------------------
    // 警告と記録
    // ------------------------------------------------------------------

    [Fact]
    public async Task 拒否されたカレンダーは原因つきの警告で他は続ける()
    {
        var lines = new List<string>();
        var handler = new RoutingHandler(url => Route(url, u => u.Contains("b%40group", StringComparison.Ordinal) || u.Contains("b@group", StringComparison.Ordinal)
            ? Json(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"insufficientPermissions"}]}}""")
            : null));
        using var service = Create(handler, lines);

        var report = await service.SyncAsync();

        var warning = Assert.Single(report!.Warnings);
        Assert.Contains("家庭", warning, StringComparison.Ordinal);
        Assert.Contains("拒否されました（403）", warning, StringComparison.Ordinal);
        Assert.Contains("insufficientPermissions", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", warning, StringComparison.Ordinal);
        Assert.Equal(3, EventCalls(handler));

        var line = Assert.Single(lines);
        Assert.StartsWith("Google 同期: 「家庭」の同期で失敗。GoogleApiException", line, StringComparison.Ordinal);
        Assert.Contains("403", line, StringComparison.Ordinal);
        Assert.Contains("種類=Forbidden", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 接続できない失敗は原因ごとの警告にして英語のメッセージを出さない()
    {
        var lines = new List<string>();
        var handler = new RoutingHandler(url => url.Contains("calendarList", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, ThreeCalendars)
            : url.Contains("/colors", StringComparison.Ordinal) || url.Contains("tasks.googleapis", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"calendar":{},"items":[]}""")
                : throw new HttpRequestException("An error occurred while sending the request.",
                    new System.Security.Authentication.AuthenticationException("The remote certificate is invalid.")));
        using var service = Create(handler, lines);

        var report = await service.SyncAsync();

        Assert.All(report!.Warnings, warning =>
        {
            Assert.Contains("証明書", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("An error occurred", warning, StringComparison.Ordinal);
        });

        Assert.Contains(lines, l => l.Contains("種類=CertificateProblem", StringComparison.Ordinal));
    }
}
