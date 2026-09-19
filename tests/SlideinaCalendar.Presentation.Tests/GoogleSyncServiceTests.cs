using System.Net;
using SlideinaCalendar.Google.Sync;
using SlideinaCalendar.Presentation.Sync;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// カレンダーごと・リストごとの同期のまとめ役。
/// <para>
/// <b>1つが読めないだけで全体を止めない</b>ことが肝心。実機で、誕生日のような
/// 特殊なカレンダーが 404 を返し、他の予定まで一件も入らない状態になった。
/// </para>
/// </summary>
public class GoogleSyncServiceTests : IDisposable
{
    private sealed class FixedToken : IAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("ya29.test");
    }

    /// <summary>行き先ごとに応答を出し分ける。</summary>
    private sealed class RoutingHandler(Func<string, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        /// <summary>応答を遅らせる。走っている最中に呼ばれる状況を作る。</summary>
        public TimeSpan Delay { get; set; }

        public List<string> Seen { get; } = [];

        /// <summary>書き込みに行った先。送っていないことを確かめるのに使う。</summary>
        public List<string> Wrote { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Seen.Add(url);

            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);

            if (request.Method != HttpMethod.Get)
            {
                Wrote.Add(url);

                // 作った・直したときの応答。id を返さないと、控えを更新できない
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"id":"created-1","summary":"送ったもの","status":"confirmed",
                         "updated":"2026-09-19T00:00:00.000Z",
                         "start":{"date":"2026-09-24"},"end":{"date":"2026-09-25"}}
                        """,
                        System.Text.Encoding.UTF8, "application/json"),
                };
            }

            var (status, body) = respond(url);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private readonly TestWorkspace _test = TestWorkspace.Create();

    public void Dispose() => _test.Dispose();

    /// <summary>「読める」「読めない」の2つを持つカレンダー一覧。</summary>
    private const string TwoCalendars = """
        {"items":[
          {"id":"yomeru@group.calendar.google.com","summary":"仕事","accessRole":"owner",
           "backgroundColor":"#2f6fed"},
          {"id":"yomenai@group.v.calendar.google.com","summary":"誕生日","accessRole":"reader"}
        ]}
        """;

    private GoogleSyncService Create(RoutingHandler handler)
    {
        var http = new HttpClient(handler);
        var token = new FixedToken();

        return new GoogleSyncService(
            _test.Workspace, new GoogleCalendarApi(http, token), new GoogleTasksApi(http, token));
    }

    private static (HttpStatusCode, string) Route(string url) => url switch
    {
        _ when url.Contains("calendarList", StringComparison.Ordinal) => (HttpStatusCode.OK, TwoCalendars),
        _ when url.Contains("/colors", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"calendar":{}}"""),

        // 誕生日のような特殊なカレンダーは、一覧に出ても中身を取れないことがある
        _ when url.Contains("yomenai", StringComparison.Ordinal) =>
            (HttpStatusCode.NotFound, """{"error":{"errors":[{"reason":"notFound"}]}}"""),

        _ when url.Contains("yomeru", StringComparison.Ordinal) => (HttpStatusCode.OK, """
            {"items":[{"id":"g1","summary":"定例","status":"confirmed",
              "start":{"date":"2026-09-24"},"end":{"date":"2026-09-25"}}],
             "nextSyncToken":"t1"}
            """),

        _ when url.Contains("users/@me/lists", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"items":[]}"""),
        _ => (HttpStatusCode.OK, """{"items":[]}"""),
    };

    [Fact]
    public async Task 読めないカレンダーがあっても他は取り込める()
    {
        using var service = Create(new RoutingHandler(Route));

        var report = await service.SyncAsync();

        Assert.NotNull(report);

        // 読めたほうの予定は入る。ここが 0 だと、1件の失敗で全滅している
        var stored = Assert.Single(_test.Workspace.Events.All());
        Assert.Equal("定例", stored.Title);
    }

    [Fact]
    public async Task 読めなかったことは黙らせない()
    {
        using var service = Create(new RoutingHandler(Route));

        var report = await service.SyncAsync();

        var warning = Assert.Single(report!.Warnings);

        // どのカレンダーが読めなかったのか分かるようにする
        Assert.Contains("誕生日", warning, StringComparison.Ordinal);
        Assert.Contains("notFound", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task カレンダー一覧を取り込む()
    {
        using var service = Create(new RoutingHandler(Route));

        await service.SyncAsync();

        var names = _test.Workspace.Sources.Calendars().Select(c => c.DisplayName).ToArray();

        Assert.Contains("仕事", names);
        Assert.Contains("誕生日", names);
    }

    [Fact]
    public async Task 読むだけのカレンダーへは送らない()
    {
        var handler = new RoutingHandler(Route);
        using var service = Create(handler);

        await service.SyncAsync();

        // accessRole が reader のカレンダーへ書き込みに行っていないこと
        Assert.DoesNotContain(handler.Wrote, url => url.Contains("yomenai", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 同時に二本走らせない()
    {
        // 遅らせないと、1本目が終わってから2本目が始まってしまい、重ならない
        var handler = new RoutingHandler(Route) { Delay = TimeSpan.FromMilliseconds(50) };
        using var service = Create(handler);

        var first = service.SyncAsync();
        var second = service.SyncAsync();

        var results = await Task.WhenAll(first, second);

        // 同じ予定を両方が書き換えると、どちらが勝ったのか分からなくなる。
        // 後から来たほうは何もせず引き下がる
        Assert.Single(results, r => r is not null);
        Assert.Single(results, r => r is null);
    }
}
