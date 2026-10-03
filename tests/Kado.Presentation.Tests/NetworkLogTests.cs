using System.Net;
using Kado.Core.Net;
using Kado.Presentation.Net;

namespace Kado.Presentation.Tests;

/// <summary>
/// 更新以外の通信（Google・実働日の配信）の失敗を、shell.log に1行で残す仕組み。
/// 更新の確認で作った記録の流儀と同じ（例外の連鎖・状態コード・種類・プロキシ経由か）。
/// </summary>
public class NetworkLogTests
{
    private sealed class FixedProxy(Uri? via, ICredentials? credentials = null) : IWebProxy
    {
        public ICredentials? Credentials { get; set; } = credentials;

        public Uri? GetProxy(Uri destination) => via;

        public bool IsBypassed(Uri host) => via is null;
    }

    [Fact]
    public void 失敗は例外の連鎖と種類を1行にする()
    {
        var lines = new List<string>();
        var log = new NetworkLog(lines.Add);

        var error = new HttpRequestException(
            "SSL 接続を確立できませんでした",
            new System.Security.Authentication.AuthenticationException("リモート証明書が無効です"));

        log.Failure("Google 同期", error, NetworkFailure.Classify(error), "「仕事」の同期");

        var line = Assert.Single(lines);
        Assert.StartsWith("Google 同期: 「仕事」の同期で失敗。", line, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException", line, StringComparison.Ordinal);
        Assert.Contains(" ← AuthenticationException", line, StringComparison.Ordinal);
        Assert.EndsWith("（種類=CertificateProblem）", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void 何をしていたかを省くと失敗とだけ出す()
    {
        var lines = new List<string>();

        new NetworkLog(lines.Add).Failure("実働日の配信", new TimeoutException("t"), NetworkFailureKind.Timeout);

        Assert.StartsWith("実働日の配信: 失敗。", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public void 経路の記録は一度だけで資格情報の設定も出る()
    {
        var lines = new List<string>();
        var log = new NetworkLog(lines.Add, new FixedProxy(new Uri("http://proxy.corp.example:8080")));

        log.LogProxyOnce("Google 通信", "https://www.googleapis.com/calendar/v3/");
        log.LogProxyOnce("Google 通信", "https://www.googleapis.com/calendar/v3/");

        var line = Assert.Single(lines);
        Assert.Equal(
            "Google 通信: プロキシを経由する（proxy.corp.example:8080, 宛先 www.googleapis.com, 資格情報=ログオン中のユーザー）",
            line);
    }

    [Fact]
    public void プロキシを経由しなければそう残す()
    {
        var lines = new List<string>();

        new NetworkLog(lines.Add, new FixedProxy(null)).LogProxyOnce("Google 通信", "https://www.googleapis.com/");

        Assert.Contains("プロキシを経由しない", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public void URLでなければ経路を調べられなかったと残して落ちない()
    {
        var lines = new List<string>();

        new NetworkLog(lines.Add).LogProxyOnce("Google 通信", "これはURLではない");

        Assert.Contains("経路を調べられなかった", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public void 書き込み先が失敗しても通信は止めない()
    {
        var log = new NetworkLog(_ => throw new IOException("disk full"));

        log.Write("Google 同期", "x");
        log.Failure("Google 同期", new InvalidOperationException("e"), NetworkFailureKind.Other);
        log.LogProxyOnce("Google 通信", "https://www.googleapis.com/");
    }

    [Fact]
    public void 書き込み先が無ければ何もしない()
    {
        NetworkLog.None.Write("a", "b");
        new NetworkLog(null).Failure("a", new InvalidOperationException("e"), NetworkFailureKind.Other);
        new NetworkLog(null).LogProxyOnce("a", "https://www.googleapis.com/");
    }

    [Fact]
    public void Retry_Afterは応答の要点に残る()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));

        Assert.Contains("Retry-After=12秒", NetworkDiagnostics.DescribeResponse(response), StringComparison.Ordinal);
    }

    [Fact]
    public void 応答を持つ例外は要点を添えて残す()
    {
        var error = new Described("応答 407 ProxyAuthenticationRequired, Content-Type=text/html");

        Assert.Contains(
            "[応答 407 ProxyAuthenticationRequired, Content-Type=text/html]",
            NetworkDiagnostics.Summarize(error), StringComparison.Ordinal);
    }

    private sealed class Described(string info) : Exception("断られた"), IResponseDescribed
    {
        public string? ResponseInfo { get; } = info;
    }
}
