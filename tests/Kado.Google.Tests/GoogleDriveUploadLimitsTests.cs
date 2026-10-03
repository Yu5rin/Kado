using System.Net;
using System.Text;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// 添付のアップロードの待ち時間。
/// <para>
/// 以前は、ほかの API と同じ全体30秒の上限が掛かっていて、遅い回線で大きなファイルを送れなかった。
/// いまは、大きさに応じた長い上限（最低2分・1MB ごとに加算・30分まで）と、進み具合が止まったときの
/// 打ち切りを持つ。ほかの API は30秒のまま。
/// </para>
/// </summary>
public class GoogleDriveUploadLimitsTests : IDisposable
{
    private sealed class FixedToken : IAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("ya29.test");
    }

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kado-upload-{Guid.NewGuid():N}.bin");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>約 300KB のファイルを作る。本文は80KB程度ずつ読まれるので、何回かに分かれる。</summary>
    private void WriteFile(int bytes = 300 * 1024) => File.WriteAllBytes(_path, new byte[bytes]);

    /// <summary>
    /// 本文を受け取る口を、決めた振る舞いの出力先へ流し込む HTTP。
    /// 流し込むと、本文を読みに来る側（アップロードの進み具合の見張り）も、それに合わせて進む／止まる。
    /// </summary>
    private sealed class SinkHandler(Func<HttpRequestMessage, Stream> createSink, Func<HttpResponseMessage>? respond = null)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            if (request.Content is not null)
            {
                await using var sink = createSink(request);
                await request.Content.CopyToAsync(sink, cancellationToken).ConfigureAwait(false);
            }

            return respond?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"file-1"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>書き込みのたびに決めた待ちを入れる。待ちの間は、本文は読まれない（ネットワークが詰まった状態）。</summary>
    private sealed class SlowSink(Func<int, CancellationToken, Task> beforeWrite, CancellationToken token) : Stream
    {
        private int _writes;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), token).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // マルチパートの境界線や見出しのような小さい書き込みは数えない。ファイルの中身（大きい書き込み）だけ
            if (buffer.Length < 4096) return;

            await beforeWrite(_writes++, cancellationToken).ConfigureAwait(false);
        }
    }

    private static GoogleDriveApi Api(HttpMessageHandler handler, TimeSpan stall, GoogleRetryPolicy? retry = null) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, new FixedToken(), retry ?? GoogleRetryPolicy.None)
        {
            UploadStallTimeout = stall,
        };

    // ------------------------------------------------------------------
    // 全体の上限
    // ------------------------------------------------------------------

    [Fact]
    public void 上限は最低2分で1MBごとに足して30分で頭打ち()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), GoogleDriveApi.UploadTimeoutFor(0));

        // 1MB 以下は1MB ぶんとして数える（切り上げ）
        Assert.Equal(TimeSpan.FromMinutes(2.5), GoogleDriveApi.UploadTimeoutFor(1));
        Assert.Equal(TimeSpan.FromMinutes(2.5), GoogleDriveApi.UploadTimeoutFor(1024 * 1024));
        Assert.Equal(TimeSpan.FromMinutes(3), GoogleDriveApi.UploadTimeoutFor(1024 * 1024 + 1));

        // 10MB
        Assert.Equal(TimeSpan.FromMinutes(7), GoogleDriveApi.UploadTimeoutFor(10L * 1024 * 1024));

        // 大きいものでも30分まで
        Assert.Equal(TimeSpan.FromMinutes(30), GoogleDriveApi.UploadTimeoutFor(500L * 1024 * 1024));
    }

    [Fact]
    public void ほかのAPIの上限は30秒のまま()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), GoogleDriveApi.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), Kado.Core.Net.KadoHttp.DefaultTimeout);
    }

    // ------------------------------------------------------------------
    // 進み具合が止まったら打ち切る
    // ------------------------------------------------------------------

    [Fact]
    public async Task 送るのが止まったら打ち切って時間切れにする()
    {
        WriteFile();

        // 1回目だけ流れて、あとは詰まる
        var handler = new SinkHandler(request =>
            new SlowSink(async (index, ct) =>
            {
                if (index >= 1) await Task.Delay(Timeout.Infinite, ct);
            }, CancellationToken.None));

        var api = Api(handler, stall: TimeSpan.FromMilliseconds(300));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => api.UploadFileAsync("folder-1", _path, "application/octet-stream"));

        Assert.Contains("進まなかった", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ゆっくりでも進んでいれば打ち切らない()
    {
        WriteFile(500 * 1024);

        // 書き込みごとに 100ms かかる。合計は止まったとみなす長さ（300ms）を超えるが、止まってはいない
        var started = DateTime.UtcNow;
        var handler = new SinkHandler(request =>
            new SlowSink((_, ct) => Task.Delay(100, ct), CancellationToken.None));

        var api = Api(handler, stall: TimeSpan.FromMilliseconds(400));

        var file = await api.UploadFileAsync("folder-1", _path, "application/octet-stream");

        Assert.Equal("file-1", file.GetProperty("id").GetString());
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(300), "送るのに時間がかかっている前提");
    }

    [Fact]
    public async Task 呼び出し側の中止は時間切れにしない()
    {
        WriteFile();
        using var cts = new CancellationTokenSource();

        var handler = new SinkHandler(request =>
            new SlowSink(async (index, ct) =>
            {
                if (index >= 1) await Task.Delay(Timeout.Infinite, ct);
            }, CancellationToken.None));

        var api = Api(handler, stall: TimeSpan.FromSeconds(30));

        var uploading = api.UploadFileAsync("folder-1", _path, "application/octet-stream", cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        // 利用者が止めたのは、失敗ではなく取り消し（TimeoutException ではない）
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uploading);
    }

    [Fact]
    public async Task 出し直しのたびにファイルを開き直して全部送る()
    {
        WriteFile(100 * 1024);

        var sizes = new List<long>();
        var calls = 0;

        var handler = new SinkHandler(
            request => new CountingSink(sizes),
            respond: () => ++calls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"file-1"}""", Encoding.UTF8, "application/json"),
                });

        var api = Api(handler, TimeSpan.FromSeconds(30), new GoogleRetryPolicy((_, _) => Task.CompletedTask));

        await api.UploadFileAsync("folder-1", _path, "application/octet-stream");

        // 2回とも、本文は同じ大きさ（ファイルの最初から最後まで）
        Assert.Equal(2, sizes.Count);
        Assert.Equal(sizes[0], sizes[1]);
        Assert.True(sizes[0] > 100 * 1024);
    }

    private sealed class CountingSink(List<long> sizes) : Stream
    {
        private long _total;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _total;

        public override long Position { get => _total; set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _total += count;

        protected override void Dispose(bool disposing)
        {
            if (disposing) sizes.Add(_total);

            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task 開けないファイルは通信を始める前に例外にする()
    {
        var handler = new SinkHandler(_ => Stream.Null);
        var api = Api(handler, TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<IOException>(
            () => api.UploadFileAsync("folder-1", _path + ".missing", "application/octet-stream"));

        Assert.Equal(0, handler.Calls);
    }

    // ------------------------------------------------------------------
    // 進み具合を見る入れ物
    // ------------------------------------------------------------------

    [Fact]
    public async Task 読み取りが進むたびに知らせ終わりでも知らせる()
    {
        var progress = 0;
        var ended = 0;

        await using var inner = new MemoryStream(new byte[10]);
        await using var stream = new ProgressStream(inner, () => progress++, () => ended++);

        var buffer = new byte[4];
        while (await stream.ReadAsync(buffer) > 0)
        {
        }

        Assert.Equal(3, progress);
        Assert.Equal(1, ended);

        // 長さと位置は中身に任せる（長さが分からないと、本文が分割転送になる）
        Assert.True(stream.CanSeek);
        Assert.Equal(10, stream.Length);
    }
}
