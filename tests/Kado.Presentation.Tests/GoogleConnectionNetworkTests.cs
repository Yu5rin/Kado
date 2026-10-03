using System.Net;
using System.Net.Http.Headers;
using Kado.Google.OAuth;
using Kado.Google.Sync;
using Kado.Presentation.Net;
using Kado.Presentation.Sync;

namespace Kado.Presentation.Tests;

/// <summary>
/// 実物の繋ぎ（<see cref="GoogleConnection"/>）の通信まわり。
/// <list type="bullet">
/// <item>最初の HTTP 送信が、呼んだ（画面の）スレッドから始まらないこと</item>
/// <item>失敗が shell.log に1行ずつ残ること（状態コード・Content-Type・経路）</item>
/// <item>呼びすぎ（429）のときに、残りを次回に回すこと</item>
/// </list>
/// </summary>
public class GoogleConnectionNetworkTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("kado-connection-").FullName;

    private readonly TestWorkspace _test = TestWorkspace.Create();

    public void Dispose()
    {
        _test.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private GoogleClientSecretsStore OwnClientFile()
    {
        var path = Path.Combine(_folder, "google-client.json");

        File.WriteAllText(path, """
            {"installed":{"client_id":"mine.apps.googleusercontent.com","client_secret":"s",
             "auth_uri":"https://accounts.google.com/o/oauth2/auth","token_uri":"https://oauth2.googleapis.com/token"}}
            """);

        return new GoogleClientSecretsStore(path);
    }

    private static InMemoryTokenStore Connected(params string[] scopes) => new(new OAuthTokens
    {
        AccessToken = "at",
        RefreshToken = "rt",
        ExpiresAt = DateTimeOffset.Now.AddHours(1),
        Scopes = scopes,
    });

    /// <summary>要求のたびに、どのスレッドで受けたかを控える。</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, int ThreadId)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Seen) Seen.Add((request.RequestUri!.ToString(), Environment.CurrentManagedThreadId));

            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Quiet(HttpRequestMessage request) =>
        request.RequestUri!.ToString() switch
        {
            var url when url.Contains("calendarList", StringComparison.Ordinal) => Json(HttpStatusCode.OK, """{"items":[]}"""),
            _ => Json(HttpStatusCode.OK, """{"items":[]}"""),
        };

    // ------------------------------------------------------------------
    // 最初の通信は、呼んだスレッドから始めない
    // ------------------------------------------------------------------

    [Fact]
    public async Task 同期の最初の通信は画面のスレッドから始まらない()
    {
        // 最初の HTTP 要求は、経路（プロキシ）の自動検出で呼んだスレッドのまま数秒止まることがある（会社の回線）
        using var ui = new SyncThreadTests.SingleThreadContext();
        var handler = new RecordingHandler(Quiet);
        using var connection = new GoogleConnection(
            _test.Workspace, OwnClientFile(), Connected(), _ => { }, new HttpClient(handler));

        await ui.RunAsync(async () => await connection.SyncAsync());

        Assert.NotEmpty(handler.Seen);
        Assert.All(handler.Seen, seen => Assert.NotEqual(ui.ThreadId, seen.ThreadId));
    }

    [Fact]
    public async Task 切断の通信は画面のスレッドから始まらない()
    {
        using var ui = new SyncThreadTests.SingleThreadContext();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var connection = new GoogleConnection(
            _test.Workspace, OwnClientFile(), Connected(), _ => { }, new HttpClient(handler));

        await ui.RunAsync(async () => await connection.DisconnectAsync());

        var seen = Assert.Single(handler.Seen);
        Assert.NotEqual(ui.ThreadId, seen.ThreadId);
    }

    [Fact]
    public async Task 接続の認可は画面のスレッドで始まらない()
    {
        // ブラウザを開く前に、受け口（HttpListener）を開く。トークン交換の通信も続く。どれも画面のスレッドから外す
        using var ui = new SyncThreadTests.SingleThreadContext();
        var openedOn = 0;

        using var connection = new GoogleConnection(
            _test.Workspace, OwnClientFile(), new InMemoryTokenStore(),
            _ =>
            {
                openedOn = Environment.CurrentManagedThreadId;
                throw new InvalidOperationException("ブラウザは開かない");
            },
            new HttpClient(new RecordingHandler(Quiet)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ui.RunAsync(async () => await connection.ConnectAsync()));

        Assert.NotEqual(0, openedOn);
        Assert.NotEqual(ui.ThreadId, openedOn);
    }

    [Fact]
    public async Task 追加認可も画面のスレッドで始まらない()
    {
        using var ui = new SyncThreadTests.SingleThreadContext();
        var openedOn = 0;

        using var connection = new GoogleConnection(
            _test.Workspace, OwnClientFile(), Connected(), _ =>
            {
                openedOn = Environment.CurrentManagedThreadId;
                throw new InvalidOperationException("ブラウザは開かない");
            },
            new HttpClient(new RecordingHandler(Quiet)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ui.RunAsync(async () => await connection.EnsureDriveAttachmentScopeAsync()));

        Assert.NotEqual(0, openedOn);
        Assert.NotEqual(ui.ThreadId, openedOn);
    }

    [Fact]
    public async Task 同期のあとは呼んだスレッドへ戻れる()
    {
        // 通信は外で始めるが、呼んだ側が画面の接続に触れるよう、await のあと呼んだスレッドへ戻れる
        using var ui = new SyncThreadTests.SingleThreadContext();
        using var connection = new GoogleConnection(
            _test.Workspace, OwnClientFile(), Connected(), _ => { }, new HttpClient(new RecordingHandler(Quiet)));

        var after = 0;
        await ui.RunAsync(async () =>
        {
            await connection.SyncAsync().ConfigureAwait(true);
            after = Environment.CurrentManagedThreadId;
        });

        Assert.Equal(ui.ThreadId, after);
    }

    // ------------------------------------------------------------------
    // shell.log
    // ------------------------------------------------------------------

    [Fact]
    public async Task プロキシの認証で断られたら応答の要点を1行で残す()
    {
        var lines = new List<string>();
        var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired)
        {
            Content = new StringContent("<html>認証してください</html>", System.Text.Encoding.UTF8, "text/html"),
            Headers = { ProxyAuthenticate = { new AuthenticationHeaderValue("Negotiate"), new AuthenticationHeaderValue("NTLM") } },
        });

        using var connection = new GoogleConnection(
            _test.Workspace, OwnClientFile(), Connected(), _ => { }, new HttpClient(handler),
            log: new NetworkLog(lines.Add), retry: GoogleRetryPolicy.None);

        // 407 は、どのカレンダーでも同じ結果。1つずつ警告にせず、同期ごと失敗にする
        var error = await Assert.ThrowsAsync<GoogleApiException>(() => connection.SyncAsync());
        Assert.True(error.IsProxyAuthRequired);

        var failure = Assert.Single(lines, l => l.StartsWith("Google 同期: ", StringComparison.Ordinal) && l.Contains("失敗", StringComparison.Ordinal));
        Assert.Contains("GoogleApiException", failure, StringComparison.Ordinal);
        Assert.Contains("407", failure, StringComparison.Ordinal);
        Assert.Contains("Content-Type=text/html", failure, StringComparison.Ordinal);
        Assert.Contains("Proxy-Authenticate=Negotiate/NTLM", failure, StringComparison.Ordinal);
        Assert.Contains("種類=ProxyAuthRequired", failure, StringComparison.Ordinal);

        // 応答の本文・トークンは書かない
        Assert.DoesNotContain(lines, l => l.Contains("認証してください", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("Bearer", StringComparison.Ordinal) || l.Contains("ya29", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 通信の失敗は例外の連鎖と種類を残す()
    {
        var lines = new List<string>();
        var handler = new ThrowingHandler(new HttpRequestException(
            "SSL 接続を確立できませんでした", new System.Security.Authentication.AuthenticationException("リモート証明書が無効です")));

        using var connection = new GoogleConnection(
            _test.Workspace, OwnClientFile(), Connected(), _ => { }, new HttpClient(handler),
            log: new NetworkLog(lines.Add), retry: GoogleRetryPolicy.None);

        await Assert.ThrowsAsync<HttpRequestException>(() => connection.SyncAsync());

        var failure = Assert.Single(lines, l => l.Contains("失敗", StringComparison.Ordinal));
        Assert.Contains("HttpRequestException", failure, StringComparison.Ordinal);
        Assert.Contains("AuthenticationException", failure, StringComparison.Ordinal);
        Assert.Contains("種類=CertificateProblem", failure, StringComparison.Ordinal);
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => throw error;
    }

    [Fact]
    public async Task 経路の記録は一度だけ()
    {
        var lines = new List<string>();
        using var connection = new GoogleConnection(
            _test.Workspace, OwnClientFile(), Connected(), _ => { }, new HttpClient(new RecordingHandler(Quiet)),
            log: new NetworkLog(lines.Add));

        await connection.SyncAsync();
        await connection.SyncAsync();

        // 「プロキシを経由する（…）」または「プロキシを経由しない（…）」のどちらか。起動ごとに1回
        Assert.Single(lines, l => l.StartsWith("Google 通信: プロキシを経由", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 利用者が止めた同期は失敗として残さない()
    {
        var lines = new List<string>();
        var handler = new RecordingHandler(Quiet);
        using var connection = new GoogleConnection(
            _test.Workspace, OwnClientFile(), Connected(), _ => { }, new HttpClient(handler),
            log: new NetworkLog(lines.Add));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.SyncAsync(cts.Token));

        Assert.DoesNotContain(lines, l => l.Contains("失敗", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("中止", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 取り消しが届かなかった切断は結果を残す()
    {
        var lines = new List<string>();
        using var connection = new GoogleConnection(
            _test.Workspace, OwnClientFile(), Connected(), _ => { },
            new HttpClient(new ThrowingHandler(new HttpRequestException("offline"))),
            log: new NetworkLog(lines.Add));

        Assert.False(await connection.DisconnectAsync());

        Assert.Contains(lines, l => l.StartsWith("Google 認可: 取り消し: 通信に失敗", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Google 切断: ", StringComparison.Ordinal) && l.Contains("届かなかった", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // 呼びすぎ
    // ------------------------------------------------------------------

    [Fact]
    public async Task 呼びすぎが続いたら残りを次回に回し間隔を延ばす合図を返す()
    {
        // カレンダー一覧は取れる。各カレンダーの中身は 429（待って出し直してもだめ）
        const string list = """
            {"items":[
              {"id":"a@group.calendar.google.com","summary":"A","accessRole":"owner"},
              {"id":"b@group.calendar.google.com","summary":"B","accessRole":"owner"},
              {"id":"c@group.calendar.google.com","summary":"C","accessRole":"owner"}]}
            """;

        var handler = new RecordingHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("calendarList", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, list);
            if (url.Contains("/colors", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, """{"calendar":{}}""");
            if (url.Contains("tasks.googleapis", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, """{"items":[]}""");

            return Json(HttpStatusCode.TooManyRequests, """{"error":{"errors":[{"reason":"rateLimitExceeded"}]}}""");
        });

        var waits = new List<TimeSpan>();
        var retry = new GoogleRetryPolicy((span, _) => { waits.Add(span); return Task.CompletedTask; });

        using var connection = new GoogleConnection(
            _test.Workspace, OwnClientFile(), Connected(), _ => { }, new HttpClient(handler), retry: retry);

        var report = await connection.SyncAsync();

        Assert.NotNull(report);
        Assert.True(report.Deferred);
        Assert.True(report.Throttled);

        // 警告は1行。カレンダーの数だけ並べない
        Assert.Equal([SyncReport.BusyWarning], report.Warnings);

        // 最初のカレンダーで待って出し直し、それでもだめなので、残りの2つは叩かない
        var eventCalls = handler.Seen.Count(s => s.Url.Contains("/events", StringComparison.Ordinal));
        Assert.Equal(1 + GoogleRetryPolicy.MaxRetries, eventCalls);
        Assert.Equal(GoogleRetryPolicy.MaxRetries, waits.Count);
    }

    [Fact]
    public async Task 同期のたびに待った合計を数え直す()
    {
        const string list = """{"items":[{"id":"a@group.calendar.google.com","summary":"A","accessRole":"owner"}]}""";

        var handler = new RecordingHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("calendarList", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, list);
            if (url.Contains("/colors", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, """{"calendar":{}}""");
            if (url.Contains("tasks.googleapis", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, """{"items":[]}""");

            var busy = Json(HttpStatusCode.TooManyRequests, "{}");
            busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return busy;
        });

        var retry = new GoogleRetryPolicy((_, _) => Task.CompletedTask);
        using var connection = new GoogleConnection(
            _test.Workspace, OwnClientFile(), Connected(), _ => { }, new HttpClient(handler), retry: retry);

        await connection.SyncAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), retry.Waited);

        // 次の同期の頭で数え直す。前回の待ちが残っていると、2回目以降は一度も待たずに諦めてしまう
        handler.Seen.Clear();
        await connection.SyncAsync();

        Assert.Equal(TimeSpan.FromSeconds(60), retry.Waited);
        Assert.True(handler.Seen.Count(s => s.Url.Contains("/events", StringComparison.Ordinal)) > 1);
    }

    // ------------------------------------------------------------------
    // 警告に型名を出さない
    // ------------------------------------------------------------------

    [Fact]
    public async Task 想定外の失敗も画面の警告に型名を出さず記録に残す()
    {
        var lines = new List<string>();

        const string list = """{"items":[{"id":"a@group.calendar.google.com","summary":"仕事","accessRole":"owner"}]}""";
        var handler = new RecordingHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("calendarList", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, list);
            if (url.Contains("/colors", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, """{"calendar":{}}""");
            if (url.Contains("tasks.googleapis", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, """{"items":[]}""");

            // 中身が壊れている（JSON として読めない）
            return Json(HttpStatusCode.OK, "<html>");
        });

        using var connection = new GoogleConnection(
            _test.Workspace, OwnClientFile(), Connected(), _ => { }, new HttpClient(handler),
            log: new NetworkLog(lines.Add));

        var report = await connection.SyncAsync();

        var warning = Assert.Single(report!.Warnings);
        Assert.Contains("仕事", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", warning, StringComparison.Ordinal);

        // 連鎖は記録のほうに残る
        Assert.True(
            lines.Any(l => l.Contains("「仕事」の同期", StringComparison.Ordinal) && l.Contains("JsonReaderException", StringComparison.Ordinal)),
            string.Join("\n", lines));
    }
}
