using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using Kado.Presentation.Update;

namespace Kado.Presentation.Tests;

/// <summary>
/// 更新の通信を shell.log の1行にする整形。
/// <para>
/// 失敗の理由が記録されていないと、会社のネットワークでだけ起きる不具合は追えない。
/// 一方で、トークンや個人情報は書かない。
/// </para>
/// </summary>
public class UpdateDiagnosticsTests
{
    [Fact]
    public void 例外の連鎖をすべて記録する()
    {
        // 外側だけでは「送信できませんでした」としか分からない。本当の理由は内側にある
        var ex = new HttpRequestException(
            "SSL 接続を確立できませんでした",
            new AuthenticationException("リモート証明書が無効です", new InvalidOperationException("信頼されないルート")));

        var line = UpdateDiagnostics.Summarize(ex);

        Assert.Contains("HttpRequestException: SSL 接続を確立できませんでした", line, StringComparison.Ordinal);
        Assert.Contains("AuthenticationException: リモート証明書が無効です", line, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: 信頼されないルート", line, StringComparison.Ordinal);

        // 外側から内側の順に、矢印でつなぐ
        Assert.True(line.IndexOf("HttpRequestException", StringComparison.Ordinal)
                    < line.IndexOf("AuthenticationException", StringComparison.Ordinal));
        Assert.True(line.IndexOf("AuthenticationException", StringComparison.Ordinal)
                    < line.IndexOf("InvalidOperationException", StringComparison.Ordinal));
        Assert.Contains(" ← ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 状態コードを持っていれば添える()
    {
        var ex = new HttpRequestException("x", null, HttpStatusCode.ProxyAuthenticationRequired);

        Assert.Contains("[HTTP 407 ProxyAuthenticationRequired]", UpdateDiagnostics.Summarize(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void ソケットの失敗まで届く()
    {
        var ex = new HttpRequestException("送信できませんでした", new SocketException((int)SocketError.ConnectionRefused));

        Assert.Contains("SocketException", UpdateDiagnostics.Summarize(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void 集約例外は中身をすべて並べる()
    {
        var ex = new AggregateException(new IOException("一つ目"), new TimeoutException("二つ目"));

        var line = UpdateDiagnostics.Summarize(ex);

        Assert.Contains("IOException: 一つ目", line, StringComparison.Ordinal);
        Assert.Contains("TimeoutException: 二つ目", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 深すぎる連鎖は途中で打ち切る()
    {
        Exception ex = new InvalidOperationException("最も内側");

        for (var i = 0; i < UpdateDiagnostics.MaxExceptionDepth + 5; i++)
        {
            ex = new InvalidOperationException($"層{i}", ex);
        }

        var line = UpdateDiagnostics.Summarize(ex);

        Assert.Contains("これ以上は省略", line, StringComparison.Ordinal);
        Assert.DoesNotContain("最も内側", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 例外が無いときも落ちない()
    {
        Assert.Equal("(例外なし)", UpdateDiagnostics.Summarize(null));
    }

    [Theory]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/123/abc?sp=r&sig=SECRET&se=2026",
        "https://objects.githubusercontent.com/github-production-release-asset/123/abc")]
    [InlineData("https://user:password@github.com/a/b?token=SECRET#frag", "https://github.com/a/b")]
    [InlineData("https://github.com:8443/a", "https://github.com:8443/a")]
    public void URLはクエリと資格情報を落として記録する(string url, string expected)
    {
        // 転送先の URL には、一時的な署名（sig=…）が付く。ログに残さない
        var safe = UpdateDiagnostics.SafeUrl(url);

        Assert.Equal(expected, safe);
        Assert.DoesNotContain("SECRET", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void URLでないものは中身を書かない()
    {
        Assert.Equal("(URLではない)", UpdateDiagnostics.SafeUrl("これは URL ではない?token=SECRET"));
        Assert.Equal("(不明)", UpdateDiagnostics.SafeUrl((Uri?)null));
    }

    [Fact]
    public void 応答の要点を1行にする()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("Via", "1.1 proxy.example");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        // 個人情報になりうるヘッダは拾わない
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=SECRET");
        response.Headers.TryAddWithoutValidation("Authorization-Info", "SECRET");

        var line = UpdateDiagnostics.DescribeResponse(response);

        Assert.Contains("応答 403 Forbidden", line, StringComparison.Ordinal);
        Assert.Contains("Content-Type=application/json", line, StringComparison.Ordinal);
        Assert.Contains("Via=1.1 proxy.example", line, StringComparison.Ordinal);
        Assert.Contains("X-RateLimit-Remaining=0", line, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 状態407なら認証の方式名だけを記録する()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired)
        {
            Content = new StringContent("<html>認証が必要です</html>", System.Text.Encoding.UTF8, "text/html"),
        };
        response.Headers.ProxyAuthenticate.Add(new AuthenticationHeaderValue("Negotiate"));
        response.Headers.ProxyAuthenticate.Add(new AuthenticationHeaderValue("Basic", "realm=\"corp-proxy\""));

        var line = UpdateDiagnostics.DescribeResponse(response);

        Assert.Contains("応答 407 ProxyAuthenticationRequired", line, StringComparison.Ordinal);
        Assert.Contains("Content-Type=text/html", line, StringComparison.Ordinal);
        Assert.Contains("Proxy-Authenticate=Negotiate/Basic", line, StringComparison.Ordinal);
        Assert.DoesNotContain("corp-proxy", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 転送されたときだけ転送先を出す()
    {
        using var same = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://github.com/a/b"),
        };

        Assert.Null(UpdateDiagnostics.DescribeRedirect("https://github.com/a/b", same));

        using var moved = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://objects.githubusercontent.com/x?sig=SECRET"),
        };

        var line = UpdateDiagnostics.DescribeRedirect("https://github.com/a/b", moved);

        Assert.Equal("転送先=https://objects.githubusercontent.com/x", line);
    }

    [Fact]
    public void プロキシを経由するかを記録する()
    {
        var target = new Uri("https://github.com/Yu5rin/Kado/releases.atom");

        var direct = UpdateDiagnostics.DescribeProxy(new FakeProxy(null), target);
        Assert.Equal("プロキシを経由しない（宛先 github.com）", direct);

        var viaProxy = UpdateDiagnostics.DescribeProxy(
            new FakeProxy(new Uri("http://proxy.corp.example:8080")) { Credentials = new NetworkCredential("u", "p") },
            target);

        Assert.Contains("プロキシを経由する（proxy.corp.example:8080", viaProxy, StringComparison.Ordinal);
        Assert.Contains("資格情報=あり", viaProxy, StringComparison.Ordinal);
        // 資格情報の中身は書かない
        Assert.DoesNotContain("u:p", viaProxy, StringComparison.Ordinal);
    }

    [Fact]
    public void 先頭がMZなら実行ファイルの形()
    {
        Assert.True(UpdateDiagnostics.LooksLikeExecutable("MZ\u0090\0"u8));
        Assert.False(UpdateDiagnostics.LooksLikeExecutable("<html>"u8));
        Assert.False(UpdateDiagnostics.LooksLikeExecutable("M"u8));
        Assert.False(UpdateDiagnostics.LooksLikeExecutable(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData("text/html; charset=utf-8", true)]
    [InlineData("TEXT/HTML", true)]
    [InlineData("application/octet-stream", false)]
    [InlineData("application/atom+xml", false)]
    [InlineData(null, false)]
    public void HTMLかを見分ける(string? contentType, bool expected)
    {
        Assert.Equal(expected, UpdateDiagnostics.IsHtml(contentType));
    }

    /// <summary>決まった行き先を返す経路。</summary>
    internal sealed class FakeProxy(Uri? via) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => via;

        public bool IsBypassed(Uri host) => via is null;
    }
}
