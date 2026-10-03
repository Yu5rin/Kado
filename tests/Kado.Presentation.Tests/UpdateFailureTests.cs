using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Kado.Presentation.Net;
using Kado.Presentation.Update;

namespace Kado.Presentation.Tests;

/// <summary>
/// 更新が失敗したときの、種類の見分けと、利用者に見せる文言。
/// <para>
/// 「ネットワークをご確認ください」だけでは、会社の回線の上限（403）やプロキシの認証（407）の
/// ときに次の一手が分からない。例外の型名は画面に出さない。
/// </para>
/// </summary>
public class UpdateFailureTests
{
    private static HttpRequestException Http(HttpStatusCode status) =>
        new($"HTTP {(int)status}", null, status);

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, NetworkFailureKind.RateLimited)]
    [InlineData((HttpStatusCode)429, NetworkFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, NetworkFailureKind.ProxyAuthRequired)]
    [InlineData(HttpStatusCode.NotFound, NetworkFailureKind.NotFound)]
    [InlineData(HttpStatusCode.BadGateway, NetworkFailureKind.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, NetworkFailureKind.ServerError)]
    [InlineData(HttpStatusCode.BadRequest, NetworkFailureKind.Other)]
    public void 状態コードから種類を決める(HttpStatusCode status, NetworkFailureKind expected)
    {
        Assert.Equal(expected, UpdateFailure.Classify(Http(status)));
    }

    [Fact]
    public void 状態403は上限だと伝える()
    {
        var message = UpdateFailure.CheckMessage(Http(HttpStatusCode.Forbidden));

        Assert.StartsWith("更新を確かめられませんでした。", message, StringComparison.Ordinal);
        Assert.Contains("回数の上限に達していました", message, StringComparison.Ordinal);
        // 自分の操作が原因ではないこと、次の一手（待つ・リリースページ）を伝える
        Assert.Contains("同じネットワークを使う人たちで共有される", message, StringComparison.Ordinal);
        Assert.Contains("自分が何度も押していなくても起こります", message, StringComparison.Ordinal);
        Assert.Contains("リリースページから直接ご確認ください", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 状態407はプロキシの認証だと伝える()
    {
        var message = UpdateFailure.CheckMessage(Http(HttpStatusCode.ProxyAuthenticationRequired));

        Assert.Contains("プロキシ", message, StringComparison.Ordinal);
        Assert.Contains("認証", message, StringComparison.Ordinal);
        Assert.DoesNotContain("上限", message, StringComparison.Ordinal);
    }

    [Fact]
    public void タイムアウトは時間内に応答が無かったと伝える()
    {
        var timeout = new TaskCanceledException("timeout", new TimeoutException());

        Assert.Equal(NetworkFailureKind.Timeout, UpdateFailure.Classify(timeout));
        Assert.Contains("時間内に応答がありませんでした", UpdateFailure.CheckMessage(timeout), StringComparison.Ordinal);
    }

    [Fact]
    public void 接続できないのは内側の例外まで見て分ける()
    {
        // 外側は「送信できませんでした」。本当の理由は内側の SocketException
        var ex = new HttpRequestException("送信できませんでした", new SocketException((int)SocketError.ConnectionRefused));

        Assert.Equal(NetworkFailureKind.CannotConnect, UpdateFailure.Classify(ex));
        Assert.Contains("接続できませんでした", UpdateFailure.CheckMessage(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void 証明書の失敗は通信検査の可能性を伝える()
    {
        var ex = new HttpRequestException(
            "SSL 接続を確立できませんでした", new AuthenticationException("リモート証明書が無効です"));

        Assert.Equal(NetworkFailureKind.CertificateProblem, UpdateFailure.Classify(ex));
        Assert.Contains("証明書", UpdateFailure.CheckMessage(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void プロキシのトンネルを張れなかった場合は407と分ける()
    {
        var tunnel = new HttpRequestException(HttpRequestError.ProxyTunnelError, "tunnel", null, null);

        Assert.Equal(NetworkFailureKind.ProxyError, UpdateFailure.Classify(tunnel));

        // 407 が付いているならそちらを優先する
        var auth = new HttpRequestException(
            HttpRequestError.ProxyTunnelError, "tunnel", null, HttpStatusCode.ProxyAuthenticationRequired);

        Assert.Equal(NetworkFailureKind.ProxyAuthRequired, UpdateFailure.Classify(auth));
    }

    [Fact]
    public void 応答を読めなかったときは読み取れなかったと伝える()
    {
        Assert.Equal(NetworkFailureKind.UnreadableResponse, UpdateFailure.Classify(new JsonException("x")));
        Assert.Equal(NetworkFailureKind.UnreadableResponse, UpdateFailure.Classify(new InvalidDataException("x")));
    }

    [Fact]
    public void Webページが返ったときはエラーページの可能性を伝える()
    {
        var ex = new UnexpectedContentException("text/html; charset=utf-8");

        Assert.Equal(NetworkFailureKind.WebPageInsteadOfFile, UpdateFailure.Classify(ex));
        Assert.Contains("差し替えられている可能性", UpdateFailure.DownloadMessage(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void 集約例外の中身も見る()
    {
        var ex = new AggregateException(Http(HttpStatusCode.ProxyAuthenticationRequired));

        Assert.Equal(NetworkFailureKind.ProxyAuthRequired, UpdateFailure.Classify(ex));
    }

    [Fact]
    public void 例外の型名は画面に出さない()
    {
        Exception[] all =
        [
            Http(HttpStatusCode.Forbidden),
            Http(HttpStatusCode.ProxyAuthenticationRequired),
            new HttpRequestException("x", new SocketException()),
            new HttpRequestException("x", new AuthenticationException("y")),
            new TaskCanceledException("t"),
            new InvalidOperationException("English message with C:\\Users\\name\\file"),
            new IOException("The process cannot access the file"),
        ];

        foreach (var ex in all)
        {
            var shown = UpdateFailure.CheckMessage(ex) + UpdateFailure.DownloadMessage(ex);

            Assert.DoesNotContain("Exception", shown, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpRequest", shown, StringComparison.Ordinal);
            Assert.DoesNotContain("Users", shown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 分類できないときは従来の文言のままにする()
    {
        var message = UpdateFailure.CheckMessage(new InvalidOperationException("boom"));

        Assert.Equal("更新を確かめられませんでした。ネットワークをご確認ください。", message);
    }

    [Fact]
    public void Kado自身が日本語で書いたメッセージはそのまま通す()
    {
        var ex = new InvalidOperationException("落としたファイルが壊れています（ハッシュが合いません）。");

        Assert.Contains("ハッシュが合いません", UpdateFailure.DownloadMessage(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void ダウンロードの失敗には手で差し替える道を添える()
    {
        var message = UpdateFailure.DownloadMessage(new InvalidOperationException("boom"));

        Assert.Contains("リリースのページから手で差し替えてください", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 確認の失敗にはリリースのページを持たせる()
    {
        var failure = UpdateFailure.From(
            Http(HttpStatusCode.Forbidden), "https://github.com/Yu5rin/Kado/releases/latest");

        Assert.Equal(NetworkFailureKind.RateLimited, failure.Kind);
        Assert.Equal("https://github.com/Yu5rin/Kado/releases/latest", failure.ReleasePageUrl);
    }
}
