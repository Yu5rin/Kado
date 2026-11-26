namespace Kado.Presentation.Update;

/// <summary>落としたファイルの大きさが、おかしいときの失敗。画面にはこの文言がそのまま出る。</summary>
public sealed class DownloadSizeException(string message) : IOException(message);

/// <summary>
/// 更新ファイルの大きさの確かめ。
/// <para>
/// SHA256 が付かない取得（API を使わずに組み立てた取得先）では、先頭が <c>MZ</c> かどうかしか
/// 中身を確かめられない。途中で切れたファイルや、極端に大きいものを置かないよう、受信した
/// バイト数を <c>Content-Length</c>（あれば）と突き合わせ、上限（<see cref="MaxBytes"/>）も設ける。
/// </para>
/// </summary>
public static class DownloadSizeCheck
{
    /// <summary>落としてよい大きさの上限（300MB）。Kado の exe は70MB前後。</summary>
    public const long MaxBytes = 300L * 1024 * 1024;

    /// <summary>宣言された大きさ（<c>Content-Length</c>）が上限を超えていれば失敗にする。</summary>
    public static void EnsureWithinLimit(long? declared)
    {
        if (declared is { } size && size > MaxBytes) throw TooLarge();
    }

    /// <summary>受信したバイト数が上限を超えていれば失敗にする（宣言が無い・嘘のときの備え）。</summary>
    public static void EnsureReceivedWithinLimit(long received)
    {
        if (received > MaxBytes) throw TooLarge();
    }

    /// <summary>受信したバイト数が <c>Content-Length</c> と食い違っていれば失敗にする。宣言が無ければ何もしない。</summary>
    public static void EnsureLengthMatches(long? declared, long received)
    {
        if (declared is { } size && size != received)
        {
            throw new DownloadSizeException(
                $"ダウンロードしたファイルの大きさが合いません（{received}バイト受け取り、{size}バイトのはず）。" +
                "途中で切れた可能性があります。もう一度お試しください。");
        }
    }

    private static DownloadSizeException TooLarge() =>
        new($"ダウンロードしようとしたファイルが大きすぎるため中止しました（上限 {MaxBytes / 1024 / 1024}MB）。");
}
