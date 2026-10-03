using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Kado.Google.OAuth;
using Kado.Google.Sync;
using Kado.Presentation.Net;

namespace Kado.Presentation.Tests;

/// <summary>
/// Google との通信の失敗の見分けと、利用者に見せる文言。
/// <para>
/// 以前は「ネットワークに繋がりません」でひとまとめだった。会社の回線では、プロキシの認証（407）・
/// 社内の通信検査（証明書）・接続できない・時間切れ・Google が拒否（403）・呼びすぎ（429）の
/// どれなのかで、次に打つ手がまったく違う。例外の型名は画面に出さない。
/// </para>
/// </summary>
public class GoogleFailureTests
{
    private static GoogleApiException Api(HttpStatusCode status, string reason = "理由なし") => new(status, reason);

    // ------------------------------------------------------------------
    // 種類の見分け
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, "理由なし", NetworkFailureKind.ProxyAuthRequired)]
    [InlineData(HttpStatusCode.TooManyRequests, "rateLimitExceeded", NetworkFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, "rateLimitExceeded", NetworkFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, "insufficientPermissions", NetworkFailureKind.Forbidden)]
    [InlineData(HttpStatusCode.Forbidden, "quotaExceeded", NetworkFailureKind.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized, "authError", NetworkFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, "notFound", NetworkFailureKind.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, "backendError", NetworkFailureKind.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "backendError", NetworkFailureKind.ServerError)]
    [InlineData(HttpStatusCode.BadRequest, "invalid", NetworkFailureKind.Other)]
    public void Googleが返した状態から種類を決める(HttpStatusCode status, string reason, NetworkFailureKind expected) =>
        Assert.Equal(expected, GoogleFailure.Classify(Api(status, reason)));

    [Fact]
    public void プロキシ経由の接続で407ならプロキシの認証()
    {
        // HTTPS は CONNECT のところで 407 になり、状態コードを持った通信の例外で来る
        var tunnel = new HttpRequestException(
            HttpRequestError.ProxyTunnelError, "The proxy tunnel request failed", null, HttpStatusCode.ProxyAuthenticationRequired);

        Assert.Equal(NetworkFailureKind.ProxyAuthRequired, GoogleFailure.Classify(tunnel));
    }

    [Fact]
    public void 証明書の失敗は社内の通信検査として見分ける()
    {
        var error = new HttpRequestException(
            "SSL 接続を確立できませんでした", new AuthenticationException("リモート証明書が無効です"));

        Assert.Equal(NetworkFailureKind.CertificateProblem, GoogleFailure.Classify(error));
    }

    [Fact]
    public void 接続できない失敗と時間切れを見分ける()
    {
        var refused = new HttpRequestException("送信できません", new SocketException((int)SocketError.ConnectionRefused));
        var timeout = new TaskCanceledException("timeout", new TimeoutException());

        Assert.Equal(NetworkFailureKind.CannotConnect, GoogleFailure.Classify(refused));
        Assert.Equal(NetworkFailureKind.Timeout, GoogleFailure.Classify(timeout));
        Assert.Equal(NetworkFailureKind.Timeout, GoogleFailure.Classify(new TimeoutException("アップロードが止まった")));
    }

    [Fact]
    public void 応答が読めない失敗を見分ける()
    {
        // キャプティブポータルが HTML を 200 で返すと、JSON として読めない
        Assert.Equal(NetworkFailureKind.UnreadableResponse, GoogleFailure.Classify(new JsonException("x")));
    }

    [Fact]
    public void 通信の例外の403はGitHubの上限ではなくGoogleの拒否と読む()
    {
        // 更新の確認では 403 は GitHub の API の上限。Google では「Google が拒否」
        var forbidden = new HttpRequestException("x", null, HttpStatusCode.Forbidden);

        Assert.Equal(NetworkFailureKind.Forbidden, GoogleFailure.Classify(forbidden));
        Assert.Equal(NetworkFailureKind.RateLimited, NetworkFailure.Classify(forbidden));
    }

    // ------------------------------------------------------------------
    // 文言
    // ------------------------------------------------------------------

    [Fact]
    public void 原因ごとに言い分ける()
    {
        var texts = new Dictionary<string, string>
        {
            ["407"] = GoogleFailure.Describe(Api(HttpStatusCode.ProxyAuthenticationRequired), "x"),
            ["証明書"] = GoogleFailure.Describe(
                new HttpRequestException("x", new AuthenticationException("y")), "x"),
            ["接続"] = GoogleFailure.Describe(
                new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)), "x"),
            ["時間切れ"] = GoogleFailure.Describe(new TaskCanceledException("t"), "x"),
            ["403"] = GoogleFailure.Describe(Api(HttpStatusCode.Forbidden, "insufficientPermissions"), "x"),
            ["429"] = GoogleFailure.Describe(Api(HttpStatusCode.TooManyRequests, "rateLimitExceeded"), "x"),
            ["5xx"] = GoogleFailure.Describe(Api(HttpStatusCode.ServiceUnavailable, "backendError"), "x"),
            ["読めない"] = GoogleFailure.Describe(new JsonException("j"), "x"),
        };

        Assert.Contains("プロキシの認証", texts["407"], StringComparison.Ordinal);
        Assert.Contains("407", texts["407"], StringComparison.Ordinal);
        Assert.Contains("証明書", texts["証明書"], StringComparison.Ordinal);
        Assert.Contains("接続できません", texts["接続"], StringComparison.Ordinal);
        Assert.Contains("時間内に応答がありませんでした", texts["時間切れ"], StringComparison.Ordinal);
        Assert.Contains("拒否", texts["403"], StringComparison.Ordinal);
        Assert.Contains("403", texts["403"], StringComparison.Ordinal);
        Assert.Contains("多すぎます", texts["429"], StringComparison.Ordinal);
        Assert.Contains("429", texts["429"], StringComparison.Ordinal);
        Assert.Contains("応答できません", texts["5xx"], StringComparison.Ordinal);
        Assert.Contains("応答を読み取れませんでした", texts["読めない"], StringComparison.Ordinal);

        // どれもほかと取り違えない
        Assert.Equal(texts.Count, texts.Values.Distinct().Count());
        Assert.DoesNotContain(texts.Values, t => t == "x");
    }

    [Fact]
    public void 型名と英語の例外メッセージは画面に出さない()
    {
        var errors = new Exception[]
        {
            new HttpRequestException("An error occurred while sending the request."),
            new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused)),
            new InvalidOperationException("Sequence contains no elements"),
            new JsonException("'<' is an invalid start of a value."),
            Api(HttpStatusCode.ProxyAuthenticationRequired),
            Api(HttpStatusCode.Forbidden, "insufficientPermissions"),
        };

        foreach (var error in errors)
        {
            var text = GoogleFailure.Describe(error, "同期できませんでした");

            Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
            Assert.DoesNotContain("An error occurred", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Sequence contains", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 種類が決まらなければ代わりの文言を使う()
    {
        Assert.Equal("同期できませんでした", GoogleFailure.Describe(new InvalidOperationException("x"), "同期できませんでした"));
    }

    [Fact]
    public void Kadoが書いた日本語の理由とGoogleの理由はそのまま通す()
    {
        Assert.Equal("Google との連携が切れました。", GoogleFailure.Describe(new OAuthException("Google との連携が切れました。"), "x"));
        Assert.Equal("許可されませんでした", GoogleFailure.Describe(new OAuthException("x", "access_denied"), "x"));

        // 403 は、Google の理由が権限・API の無効・容量の見分けになる
        Assert.Contains("insufficientPermissions", GoogleFailure.Describe(Api(HttpStatusCode.Forbidden, "insufficientPermissions"), "x"), StringComparison.Ordinal);

        // 404 や種類を決められない 4xx は、Google の理由がいちばんの手掛かり
        Assert.Equal("notFound", GoogleFailure.Describe(Api(HttpStatusCode.NotFound, "notFound"), "x"));
        Assert.Equal("invalid", GoogleFailure.Describe(Api(HttpStatusCode.BadRequest, "invalid"), "x"));
    }

    [Fact]
    public void 長い言い方は次の一手まで添える()
    {
        Assert.Contains("管理者", GoogleFailure.LongReason(NetworkFailureKind.ProxyAuthRequired), StringComparison.Ordinal);
        Assert.Contains("検査", GoogleFailure.LongReason(NetworkFailureKind.CertificateProblem), StringComparison.Ordinal);
        Assert.Contains("許可されているか", GoogleFailure.LongReason(NetworkFailureKind.CannotConnect), StringComparison.Ordinal);
        Assert.Contains("タイムアウト", GoogleFailure.LongReason(NetworkFailureKind.Timeout), StringComparison.Ordinal);
        Assert.Contains("429", GoogleFailure.LongReason(NetworkFailureKind.RateLimited), StringComparison.Ordinal);
        Assert.Equal(string.Empty, GoogleFailure.LongReason(NetworkFailureKind.Other));
    }
}
