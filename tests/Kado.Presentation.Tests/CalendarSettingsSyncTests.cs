using System.Net;
using System.Text.Json.Nodes;
using Kado.Data.Models;
using Kado.Google.Sync;
using Kado.Presentation.Sync;

namespace Kado.Presentation.Tests;

/// <summary>
/// カレンダーの色と呼び名の書き戻し。
/// <para>
/// <b>手元で変えたときだけ送る。</b>Google の Web で色や呼び名を変えたのに、手元が旧いままだと
/// 「こちらで変えた」と取り違えて旧い値を送り返し、同期のたびに Web での変更を戻していた。
/// 「こちらで変えた」は、<b>最後に Google から受け取った姿（GoogleRaw）</b>と手元を比べて決める。
/// </para>
/// </summary>
public class CalendarSettingsSyncTests : IDisposable
{
    private const string CalendarId = "shigoto@group.calendar.google.com";

    /// <summary>色番号1が薄い青、10が緑。</summary>
    private const string Colors = """
        {"calendar":{"1":{"background":"#ac725e"},"5":{"background":"#f83a22"},
                     "10":{"background":"#16a765"},"9":{"background":"#9fc6e7"}}}
        """;

    private sealed class FixedToken : IAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("ya29.test");
    }

    private sealed class Handler(string calendarListItem) : HttpMessageHandler
    {
        /// <summary>一覧側のカレンダー設定への PATCH の本文。</summary>
        public List<JsonObject> CalendarListPatches { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (request.Method == HttpMethod.Patch && url.Contains("calendarList", StringComparison.Ordinal))
            {
                CalendarListPatches.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());

                return Json("""{"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner"}""");
            }

            if (url.Contains("calendarList", StringComparison.Ordinal))
                return Json($$"""{"items":[{{calendarListItem}}]}""");

            if (url.Contains("/colors", StringComparison.Ordinal)) return Json(Colors);

            return Json("""{"items":[]}""");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    private readonly TestWorkspace _test = TestWorkspace.Create();

    public void Dispose() => _test.Dispose();

    /// <summary>最後に Google から受け取った姿と、手元の値を置く。</summary>
    private void Seed(string receivedRaw, string? background, string? summaryOverride = null)
    {
        _test.Workspace.Sources.Upsert(new CalendarSource
        {
            Id = CalendarId,
            Summary = "仕事",
            SummaryOverride = summaryOverride,
            BackgroundColor = background,
            GoogleRaw = receivedRaw,
            UpdatedAt = DateTimeOffset.Now,
        });
    }

    private async Task<Handler> SyncAsync(string googleNow)
    {
        var handler = new Handler(googleNow);
        var http = new HttpClient(handler);
        var token = new FixedToken();

        using var service = new GoogleSyncService(
            _test.Workspace, new GoogleCalendarApi(http, token, GoogleRetryPolicy.None), new GoogleTasksApi(http, token, GoogleRetryPolicy.None));
        await service.SyncAsync();

        return handler;
    }

    private CalendarSource Stored() => _test.Workspace.Sources.FindCalendar(CalendarId)!;

    [Fact]
    public async Task Googleで色を変えたら手元の旧い色で上書きせずGoogleに従う()
    {
        // 手元は旧いまま（受け取った姿と同じ）。Web で緑に変わった
        Seed("""{"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner","backgroundColor":"#ac725e","colorId":"1"}""",
            background: "#ac725e");

        var handler = await SyncAsync(
            """{"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner","backgroundColor":"#16a765","colorId":"10"}""");

        Assert.Empty(handler.CalendarListPatches);
        Assert.Equal("#16a765", Stored().BackgroundColor);
    }

    [Fact]
    public async Task Googleで呼び名を変えたら手元の旧い呼び名で上書きせずGoogleに従う()
    {
        Seed("""{"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner","summaryOverride":"旧い呼び名"}""",
            background: null, summaryOverride: "旧い呼び名");

        var handler = await SyncAsync(
            """{"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner","summaryOverride":"新しい呼び名"}""");

        Assert.Empty(handler.CalendarListPatches);
        Assert.Equal("新しい呼び名", Stored().SummaryOverride);
    }

    [Fact]
    public async Task Googleで呼び名を外したときも手元の旧い呼び名を送り返さない()
    {
        Seed("""{"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner","summaryOverride":"旧い呼び名"}""",
            background: null, summaryOverride: "旧い呼び名");

        var handler = await SyncAsync(
            """{"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner"}""");

        Assert.Empty(handler.CalendarListPatches);
        Assert.Null(Stored().SummaryOverride);
    }

    [Fact]
    public async Task 手元で色を変えたときだけ送る()
    {
        // 受け取った姿は茶(#ac725e)。手元で赤に変えた
        Seed("""{"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner","backgroundColor":"#ac725e","colorId":"1"}""",
            background: "#f83a22");

        var handler = await SyncAsync(
            """{"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner","backgroundColor":"#ac725e","colorId":"1"}""");

        var body = Assert.Single(handler.CalendarListPatches);
        Assert.Equal("5", (string?)body["colorId"]);
        Assert.False(body.ContainsKey("summaryOverride"));
    }

    [Fact]
    public async Task 手元で呼び名を変えたときだけ送る()
    {
        Seed("""{"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner","backgroundColor":"#ac725e","colorId":"1"}""",
            background: "#ac725e", summaryOverride: "私の仕事");

        var handler = await SyncAsync(
            """{"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner","backgroundColor":"#ac725e","colorId":"1"}""");

        var body = Assert.Single(handler.CalendarListPatches);
        Assert.Equal("私の仕事", (string?)body["summaryOverride"]);
        Assert.False(body.ContainsKey("colorId"));
    }

    [Fact]
    public async Task 手元で呼び名を消したときは消す意思として送る()
    {
        // 受け取った姿には呼び名がある。手元で空にした（使う人が外した）
        Seed("""{"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner","summaryOverride":"旧い呼び名"}""",
            background: null, summaryOverride: null);

        var handler = await SyncAsync(
            """{"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner","summaryOverride":"旧い呼び名"}""");

        var body = Assert.Single(handler.CalendarListPatches);
        Assert.True(body.ContainsKey("summaryOverride"));
        Assert.Null(body["summaryOverride"]);
    }
}
