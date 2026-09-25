using System.Net;
using Kado.Data.Repositories;
using Kado.Google.OAuth;
using Kado.Google.Sync;
using Kado.Presentation.Editing;
using Kado.Presentation.Sync;

namespace Kado.Presentation.Tests;

/// <summary>
/// 添付を Google ドライブへ上げる、実物の実装。
/// <para>
/// 通信は挟まず、決まった応答を返す HTTP で確かめる。フォルダ ID を設定に控えて
/// 使い回すところが肝心（drive.file 権限では自分で作ったフォルダしか見えないため）。
/// </para>
/// </summary>
public class GoogleDriveAttachmentUploaderTests : IDisposable
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

    /// <summary>要求ごとに応答を出し分ける。</summary>
    private sealed class RoutingHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond)
        : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());

            var (status, body) = respond(request);

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeGoogleSync(HttpMessageHandler handler) : IGoogleSync
    {
        public bool IsConnected { get; set; } = true;

        public bool CanConnect => true;

        public bool HasDriveAttachmentScope { get; set; } = true;

        public bool ScopeGrantedOnDemand { get; set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SyncReport?>(null);

        public Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default)
        {
            HasDriveAttachmentScope = ScopeGrantedOnDemand;
            return Task.FromResult(HasDriveAttachmentScope);
        }

        public GoogleDriveApi CreateDriveApi() => new(new HttpClient(handler), new FixedToken());
    }

    private static string TempFile(string name = "資料.pdf")
    {
        var path = Path.Combine(Path.GetTempPath(), $"kado-{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, "テスト用の中身");
        return path;
    }

    [Fact]
    public async Task 初回はフォルダを作って設定に控える()
    {
        var handler = new RoutingHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/files", StringComparison.Ordinal) && !url.Contains("upload", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """{"id":"folder-1","name":"Kado"}""");
            }

            return (HttpStatusCode.OK, """
                {"id":"file-1","name":"資料.pdf","mimeType":"application/pdf",
                 "webViewLink":"https://drive.google.com/file/d/file-1/view"}
                """);
        });

        var google = new FakeGoogleSync(handler);
        var uploader = new GoogleDriveAttachmentUploader(google, Settings);

        var path = TempFile();
        try
        {
            var result = await uploader.UploadAsync(path);

            Assert.True(result.Succeeded);
            Assert.Equal("file-1", result.Attachment!.FileId);
            Assert.StartsWith("https://", result.Attachment.FileUrl, StringComparison.Ordinal);

            // 控えたフォルダ ID を次から使い回す
            Assert.Equal("folder-1", Settings.Get(GoogleDriveAttachmentUploader.FolderIdKey));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task 控えているフォルダIDがあれば作り直さない()
    {
        Settings.Set(GoogleDriveAttachmentUploader.FolderIdKey, "folder-cached");

        var handler = new RoutingHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            // フォルダ作成（POST .../files、upload を含まない）が来たら失敗
            if (url.Contains("/files", StringComparison.Ordinal) && !url.Contains("upload", StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                return (HttpStatusCode.InternalServerError, """{"error":{"errors":[{"reason":"unexpected"}]}}""");
            }

            return (HttpStatusCode.OK, """{"id":"file-2","name":"資料.pdf","webViewLink":"https://drive.google.com/file/d/file-2/view"}""");
        });

        var google = new FakeGoogleSync(handler);
        var uploader = new GoogleDriveAttachmentUploader(google, Settings);

        var path = TempFile();
        try
        {
            var result = await uploader.UploadAsync(path);

            Assert.True(result.Succeeded);
            Assert.DoesNotContain(handler.Urls, u => u.Contains("/files?fields=id,name", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task 権限が無ければ追加認可を試みる()
    {
        var handler = new RoutingHandler(_ => (HttpStatusCode.OK, """{"id":"folder-1"}"""));

        var google = new FakeGoogleSync(handler) { HasDriveAttachmentScope = false, ScopeGrantedOnDemand = false };
        var uploader = new GoogleDriveAttachmentUploader(google, Settings);

        var path = TempFile();
        try
        {
            var result = await uploader.UploadAsync(path);

            Assert.False(result.Succeeded);
            Assert.Contains("権限", result.ErrorMessage, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task 未接続なら理由を出す()
    {
        var google = new FakeGoogleSync(new RoutingHandler(_ => (HttpStatusCode.OK, "{}"))) { IsConnected = false };
        var uploader = new GoogleDriveAttachmentUploader(google, Settings);

        var path = TempFile();
        try
        {
            var result = await uploader.UploadAsync(path);

            Assert.False(result.Succeeded);
            Assert.Contains("接続", result.ErrorMessage, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
