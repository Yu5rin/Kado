namespace Kado.Google.Sync;

/// <summary>
/// 読み取りが進むたびに知らせる、読み取り専用のストリーム。
/// <para>
/// アップロードの本文に使う。ネットワークが詰まると、本文を読みに来る側（HTTP の送信）が止まる。
/// 「最後に読まれた時刻」を見れば、<b>送るのが止まったこと</b>を、全体の上限を待たずに見つけられる。
/// 長さと位置は中身に任せる（長さが分からないと、本文が分割転送になって Google 側の扱いが変わる）。
/// </para>
/// </summary>
internal sealed class ProgressStream(Stream inner, Action onProgress, Action onEnd) : Stream
{
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Report(_inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Report(_inner.Read(buffer));

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Report(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Report(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    /// <summary>読めたら進み、0 バイトなら終わり。どちらも相手に知らせる。</summary>
    private int Report(int read)
    {
        try
        {
            if (read > 0) onProgress();
            else onEnd();
        }
        catch (ObjectDisposedException)
        {
            // 待ちの側がもう片付いている。読み取りそのものは邪魔しない
        }

        return read;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
