using System.Net;
using Kado.Core.Net;

namespace Kado.Core.Tests;

/// <summary>
/// すべての通信の作り方（<see cref="KadoHttp"/>）。
/// <para>
/// 会社の回線は、ログオン中のユーザーの資格情報での認証を求めるプロキシ（407）を通すことが多い。
/// 何も渡さないと、Google の同期も更新の確認も実働日の配信も、どれも通らない。
/// </para>
/// </summary>
public class KadoHttpTests
{
    [Fact]
    public void プロキシへログオン中のユーザーの資格情報を渡す()
    {
        using var handler = KadoHttp.CreateHandler();

        Assert.True(handler.UseProxy);
        Assert.Same(CredentialCache.DefaultCredentials, handler.DefaultProxyCredentials);
    }

    [Fact]
    public void 宛先のサーバーへは資格情報を渡さない()
    {
        // 渡すのはシステムのプロキシにだけ。Google や GitHub へ Windows の資格情報を送らない
        using var handler = KadoHttp.CreateHandler();

        Assert.Null(handler.Credentials);
        Assert.Null(handler.Proxy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void リダイレクトの辿り方を選べる(bool allow)
    {
        using var handler = KadoHttp.CreateHandler(allow);

        Assert.Equal(allow, handler.AllowAutoRedirect);
        Assert.Same(CredentialCache.DefaultCredentials, handler.DefaultProxyCredentials);
    }

    [Fact]
    public void 既定の待ち時間は30秒()
    {
        using var client = KadoHttp.CreateClient();

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }

    [Fact]
    public void 待ち時間を無限にもできる()
    {
        // 要求ごとに自分で上限を掛ける呼び出し（更新の確認・添付のアップロード）のため
        using var client = KadoHttp.CreateClient(Timeout.InfiniteTimeSpan);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public void 常駐しても経路の変化に追いつくよう接続を使い回し続けない()
    {
        using var handler = KadoHttp.CreateHandler();

        Assert.Equal(KadoHttp.PooledConnectionLifetime, handler.PooledConnectionLifetime);
        Assert.True(handler.PooledConnectionLifetime < TimeSpan.FromHours(1));
    }

    [Fact]
    public void 通信の記録には資格情報の設定が出る()
    {
        // HttpClient.DefaultProxy は資格情報を持たない。ハンドラの設定を伝えないと「資格情報=なし」と出て食い違う
        var proxy = new FixedProxy(new Uri("http://proxy.corp.example:8080"));

        Assert.Contains("資格情報=なし", NetworkDiagnostics.DescribeProxy(proxy, new Uri("https://www.googleapis.com/")), StringComparison.Ordinal);
        Assert.Contains(
            "資格情報=ログオン中のユーザー",
            NetworkDiagnostics.DescribeProxy(proxy, new Uri("https://www.googleapis.com/"), KadoHttp.UsesDefaultProxyCredentials),
            StringComparison.Ordinal);
    }

    private sealed class FixedProxy(Uri via) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => via;

        public bool IsBypassed(Uri host) => false;
    }
}
