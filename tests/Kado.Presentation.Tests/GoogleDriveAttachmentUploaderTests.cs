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

        /// <summary>追加認可を求めたときに投げさせる。</summary>
        public Exception? ThrowOnEnsureScope { get; set; }

        /// <summary>ドライブ API を組み立てるときに投げさせる（クライアント設定が無いときの Provider()）。</summary>
        public Exception? ThrowOnCreateApi { get; set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> DisconnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SyncReport?>(null);

        public Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnEnsureScope is { } error) throw error;

            HasDriveAttachmentScope = ScopeGrantedOnDemand;
            return Task.FromResult(HasDriveAttachmentScope);
        }

        public GoogleDriveApi CreateDriveApi()
        {
            if (ThrowOnCreateApi is { } error) throw error;

            return new GoogleDriveApi(new HttpClient(handler), new FixedToken());
        }
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

    // ------------------------------------------------------------------
    // 例外を漏らさない（画面の async void から呼ばれるので、漏れるとアプリごと終わる）
    // ------------------------------------------------------------------

    private static RoutingHandler FolderThenFileHandler() => new(request =>
    {
        var url = request.RequestUri!.ToString();

        if (url.Contains("/files", StringComparison.Ordinal) && !url.Contains("upload", StringComparison.Ordinal))
        {
            return (HttpStatusCode.OK, """{"id":"folder-1","name":"Kado"}""");
        }

        return (HttpStatusCode.OK, """{"id":"file-1","name":"資料.pdf"}""");
    });

    private async Task<AttachmentUploadResult> UploadWith(FakeGoogleSync google, string? path = null)
    {
        var uploader = new GoogleDriveAttachmentUploader(google, Settings);
        var file = path ?? TempFile();

        try
        {
            return await uploader.UploadAsync(file);
        }
        finally
        {
            if (path is null) File.Delete(file);
        }
    }

    [Fact]
    public async Task 追加認可でトークン更新に失敗しても例外を漏らさない()
    {
        var google = new FakeGoogleSync(FolderThenFileHandler())
        {
            HasDriveAttachmentScope = false,
            ThrowOnEnsureScope = new OAuthException("Google との連携が切れました。接続し直してください。"),
        };

        var result = await UploadWith(google);

        Assert.False(result.Succeeded);
        Assert.Contains("接続し直してください", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task クライアント設定が無くてAPIを組み立てられなくても例外を漏らさない()
    {
        // GoogleConnection.Provider() が OAuthException を投げる。以前は try の外だった
        var google = new FakeGoogleSync(FolderThenFileHandler())
        {
            ThrowOnCreateApi = new OAuthException("Google のクライアント設定がありません。"),
        };

        var result = await UploadWith(google);

        Assert.False(result.Succeeded);
        Assert.Contains("クライアント設定", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task プロキシがHTMLを返しても例外を漏らさない()
    {
        // 200 で HTML。JSON として読めず JsonException になる
        var handler = new RoutingHandler(_ => (HttpStatusCode.OK, "<html><body>認証してください</body></html>"));

        var result = await UploadWith(new FakeGoogleSync(handler));

        Assert.False(result.Succeeded);
        Assert.Contains("応答を読み取れません", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonException", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 応答にidが無くても例外を漏らさない()
    {
        var handler = new RoutingHandler(_ => (HttpStatusCode.OK, "{}"));

        var result = await UploadWith(new FakeGoogleSync(handler));

        Assert.False(result.Succeeded);
        Assert.Contains("情報が含まれていません", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 空の応答でも例外を漏らさない()
    {
        // 本文が空だと JsonElement が default になり、GetProperty が InvalidOperationException になる
        var handler = new RoutingHandler(_ => (HttpStatusCode.OK, string.Empty));

        var result = await UploadWith(new FakeGoogleSync(handler));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task 読めないファイルでも例外を漏らさない()
    {
        // フォルダのパスを渡すと、開こうとして UnauthorizedAccessException になる（Linux）。
        // 実機では、権限が無い・別のアプリが掴んでいるファイルに当たる
        var directory = Directory.CreateTempSubdirectory("kado-attach-").FullName;
        try
        {
            var result = await UploadWith(new FakeGoogleSync(FolderThenFileHandler()), directory);

            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task ブラウザを起動できなくても例外を漏らさない()
    {
        var google = new FakeGoogleSync(FolderThenFileHandler())
        {
            HasDriveAttachmentScope = false,
            ThrowOnEnsureScope = new System.ComponentModel.Win32Exception(1260),
        };

        var result = await UploadWith(google);

        Assert.False(result.Succeeded);
        Assert.Contains("ブラウザを開けません", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 認可の受け口を開けなくても例外を漏らさない()
    {
        // HttpListenerException は Win32Exception の一種。ブラウザの案内と取り違えない
        var google = new FakeGoogleSync(FolderThenFileHandler())
        {
            HasDriveAttachmentScope = false,
            ThrowOnEnsureScope = new HttpListenerException(5),
        };

        var result = await UploadWith(google);

        Assert.False(result.Succeeded);
        Assert.Contains("認可を受け取る準備", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 利用者の取り消しは失敗にせず取り消しのまま返す()
    {
        using var cts = new CancellationTokenSource();
        var google = new FakeGoogleSync(FolderThenFileHandler())
        {
            HasDriveAttachmentScope = false,
            ThrowOnEnsureScope = new OperationCanceledException(cts.Token),
        };
        cts.Cancel();

        var uploader = new GoogleDriveAttachmentUploader(google, Settings);
        var path = TempFile();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uploader.UploadAsync(path, cts.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task 通信の時間切れは取り消しではなく失敗として返す()
    {
        // 呼び出し側は取り消していない（HttpClient のタイムアウトで TaskCanceledException）
        var google = new FakeGoogleSync(FolderThenFileHandler())
        {
            HasDriveAttachmentScope = false,
            ThrowOnEnsureScope = new TaskCanceledException("timeout"),
        };

        var result = await UploadWith(google);

        Assert.False(result.Succeeded);
        Assert.Contains("タイムアウト", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 設定の読み書きは呼ばれたスレッドへ戻ってから行う()
    {
        // 画面側の接続は呼んだスレッドで使うもの。通信から戻ったスレッドプール上で触ってはいけない。
        // 設定表への書き込みのたびに、書き込んだスレッドを控える仕掛け（SQLite の関数＋トリガー）を置く
        using var ui = new SyncThreadTests.SingleThreadContext();
        var writerThreads = new List<int>();

        _connection.CreateFunction("kado_probe", () =>
        {
            lock (writerThreads) writerThreads.Add(Environment.CurrentManagedThreadId);
            return 1;
        });
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                "CREATE TRIGGER probe_settings BEFORE INSERT ON settings BEGIN SELECT kado_probe(); END;";
            command.ExecuteNonQuery();
        }

        var handler = new ThreadHoppingHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            return url.Contains("/files", StringComparison.Ordinal) && !url.Contains("upload", StringComparison.Ordinal)
                ? """{"id":"folder-1","name":"Kado"}"""
                : """{"id":"file-1","name":"資料.pdf"}""";
        });

        var google = new FakeGoogleSync(handler);
        var uploader = new GoogleDriveAttachmentUploader(google, Settings);
        var path = TempFile();

        try
        {
            AttachmentUploadResult? result = null;
            await ui.RunAsync(async () => result = await uploader.UploadAsync(path));

            Assert.True(result!.Succeeded);

            // フォルダ ID の控えを書いたのは1回。そのスレッドが、呼んだスレッドであること
            Assert.Single(writerThreads);
            Assert.Equal(ui.ThreadId, writerThreads[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>応答を別のスレッドで返す。実際の通信と同じく、呼んだスレッドから離れる。</summary>
    private sealed class ThreadHoppingHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 要求を出したスレッドで中身を決め、そのあと別のスレッドへ移ってから返す
            var body = respond(request);

            await Task.Run(() => Thread.Sleep(5), cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
