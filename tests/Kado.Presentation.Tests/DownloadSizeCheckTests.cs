using Kado.Presentation.Update;

namespace Kado.Presentation.Tests;

/// <summary>SHA256 なしの手動更新で、受け取ったファイルの大きさを確かめる。</summary>
public class DownloadSizeCheckTests
{
    [Fact]
    public void 宣言の大きさと受け取った大きさが合えば通る()
    {
        DownloadSizeCheck.EnsureLengthMatches(70_000_000, 70_000_000);
    }

    [Theory]
    [InlineData(70_000_000L, 12_345L)]
    [InlineData(1_000L, 2_000L)]
    public void 宣言と食い違えば失敗にする(long declared, long received)
    {
        var e = Assert.Throws<DownloadSizeException>(() => DownloadSizeCheck.EnsureLengthMatches(declared, received));

        Assert.Contains("大きさが合いません", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 宣言が無ければ突き合わせない()
    {
        DownloadSizeCheck.EnsureLengthMatches(null, 12_345);
    }

    [Fact]
    public void 上限ちょうどは通り超えると断る()
    {
        DownloadSizeCheck.EnsureWithinLimit(DownloadSizeCheck.MaxBytes);
        DownloadSizeCheck.EnsureReceivedWithinLimit(DownloadSizeCheck.MaxBytes);

        Assert.Throws<DownloadSizeException>(() => DownloadSizeCheck.EnsureWithinLimit(DownloadSizeCheck.MaxBytes + 1));
        Assert.Throws<DownloadSizeException>(
            () => DownloadSizeCheck.EnsureReceivedWithinLimit(DownloadSizeCheck.MaxBytes + 1));
    }

    [Fact]
    public void 宣言が無いときの上限は受信中に見張る()
    {
        DownloadSizeCheck.EnsureWithinLimit(null);
    }

    [Fact]
    public void 失敗の文言は日本語で画面にそのまま出せる()
    {
        var ex = new DownloadSizeException("ダウンロードしたファイルの大きさが合いません");

        Assert.Equal(ex.Message, UpdateFailure.Reason(ex));
    }

    [Fact]
    public void 上限は300MB()
    {
        Assert.Equal(300L * 1024 * 1024, DownloadSizeCheck.MaxBytes);
    }
}
