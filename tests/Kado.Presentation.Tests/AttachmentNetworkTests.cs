using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Kado.Data.Repositories;
using Kado.Google.OAuth;
using Kado.Google.Sync;
using Kado.Presentation.Editing;
using Kado.Presentation.Net;
using Kado.Presentation.Sync;

namespace Kado.Presentation.Tests;

/// <summary>
/// 添付のアップロードの通信まわり。
/// <list type="bullet">
/// <item>最初の HTTP 送信が画面のスレッドから始まらないこと（設定の読み書きは画面のスレッドのまま）</item>
/// <item>失敗が原因ごとの文言になり、shell.log に1行残ること</item>
/// </list>
/// </summary>
public class AttachmentNetworkTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection =
        Kado.Data.CalendarDatabase.OpenInMemory().ConnectAndMigrate();

    private SettingsRepository Settings => new(_connection);

    public void Dispose() => _connection.Dispose();

    private sealed class FixedToken : IAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("ya29.test");
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<int> Threads { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Threads) Threads.Add(Environment.CurrentManagedThreadId);

            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeGoogleSync(HttpMessageHandler handler) : IGoogleSync
    {
        public bool IsConnected => true;

        public bool CanConnect => true;

        public bool HasDriveAttachmentScope => true;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> DisconnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SyncReport?>(null);

        public Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public GoogleDriveApi CreateDriveApi() =>
            new(new HttpClient(handler), new FixedToken(), GoogleRetryPolicy.None);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kado-{Guid.NewGuid():N}-資料.pdf");
        File.WriteAllText(path, "テスト用の中身");
        return path;
    }

    // ------------------------------------------------------------------
    // スレッド
    // ------------------------------------------------------------------

    [Fact]
    public async Task 添付の最初の通信は画面のスレッドから始まらない()
    {
        using var ui = new SyncThreadTests.SingleThreadContext();
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/files", StringComparison.Ordinal) && request.RequestUri.Query.Contains("fields=id,name", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"id":"folder-1","name":"Kado"}""")
                : Json(HttpStatusCode.OK, """{"id":"file-1","name":"資料.pdf"}"""));

        var uploader = new GoogleDriveAttachmentUploader(new FakeGoogleSync(handler), Settings);
        var path = TempFile();

        try
        {
            AttachmentUploadResult? result = null;
            await ui.RunAsync(async () => result = await uploader.UploadAsync(path));

            Assert.True(result!.Succeeded);

            // フォルダを作る通信も、ファイルを上げる通信も、画面のスレッドでは始まっていない
            Assert.Equal(2, handler.Threads.Count);
            Assert.All(handler.Threads, thread => Assert.NotEqual(ui.ThreadId, thread));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------
    // 文言と記録
    // ------------------------------------------------------------------

    [Fact]
    public void 失敗は原因ごとに言い分ける()
    {
        var proxy = AttachmentFailure.Describe(new GoogleApiException(HttpStatusCode.ProxyAuthenticationRequired, "理由なし"));
        var certificate = AttachmentFailure.Describe(new HttpRequestException("x", new AuthenticationException("y")));
        var unreachable = AttachmentFailure.Describe(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)));
        var forbidden = AttachmentFailure.Describe(new GoogleApiException(HttpStatusCode.Forbidden, "insufficientPermissions"));
        var tooMany = AttachmentFailure.Describe(new GoogleApiException(HttpStatusCode.TooManyRequests, "rateLimitExceeded"));
        var unknown = AttachmentFailure.Describe(new HttpRequestException("An error occurred."));

        Assert.Contains("プロキシ", proxy, StringComparison.Ordinal);
        Assert.Contains("407", proxy, StringComparison.Ordinal);
        Assert.Contains("証明書", certificate, StringComparison.Ordinal);
        Assert.Contains("接続できませんでした", unreachable, StringComparison.Ordinal);
        Assert.Contains("403", forbidden, StringComparison.Ordinal);
        Assert.Contains("429", tooMany, StringComparison.Ordinal);

        // 種類を決められない通信の失敗も、英語のメッセージは出さない。「オフライン」と決めつけもしない
        Assert.DoesNotContain("An error occurred", unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("オフライン", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void 送るのが止まって打ち切ったことは理由を添えて伝える()
    {
        var text = AttachmentFailure.Describe(new TimeoutException("アップロードが60秒間進まなかったので中止しました。"));

        Assert.Contains("タイムアウト", text, StringComparison.Ordinal);
        Assert.Contains("60秒間進まなかった", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 英語の時間切れのメッセージは出さない()
    {
        var text = AttachmentFailure.Describe(new TimeoutException("The operation has timed out."));

        Assert.DoesNotContain("The operation", text, StringComparison.Ordinal);
        Assert.Contains("タイムアウト", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 失敗は種類つきで1行残す()
    {
        var lines = new List<string>();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired)
        {
            Content = new StringContent("<html>x</html>", System.Text.Encoding.UTF8, "text/html"),
        });

        var uploader = new GoogleDriveAttachmentUploader(new FakeGoogleSync(handler), Settings, new NetworkLog(lines.Add));
        var path = TempFile();

        try
        {
            var result = await uploader.UploadAsync(path);

            Assert.False(result.Succeeded);
            Assert.Contains("プロキシ", result.ErrorMessage, StringComparison.Ordinal);

            var failure = Assert.Single(lines, l => l.StartsWith("Google 添付: ", StringComparison.Ordinal));
            Assert.Contains("アップロード", failure, StringComparison.Ordinal);
            Assert.Contains("407", failure, StringComparison.Ordinal);
            Assert.Contains("種類=ProxyAuthRequired", failure, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
