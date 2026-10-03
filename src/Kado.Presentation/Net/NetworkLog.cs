using System.Net;
using Kado.Core.Net;

namespace Kado.Presentation.Net;

/// <summary>
/// Google・実働日の配信など、更新以外の通信の失敗を shell.log に1行ずつ残す。
/// <para>
/// 更新の確認で作った記録の流儀（<see cref="NetworkDiagnostics"/>）を、そのまま共通にしてある。
/// 1行に、例外の連鎖（<c>InnerException</c> すべて）・状態コード・Content-Type・経由（Via）・
/// 種類・プロキシ経由かを残す。会社のネットワークでだけ起きる不具合を、理由まで追えるようにするため。
/// </para>
/// <para>
/// <b>トークンや個人情報は書かない。</b>URL はホストと道筋だけ（<see cref="NetworkDiagnostics.SafeUrl(Uri?)"/>）。
/// 本文は書かない。書き込み先（<c>sink</c>）が失敗しても、通信そのものは止めない。
/// </para>
/// </summary>
/// <param name="sink">1行ずつ渡す先（App では shell.log）。null なら何もしない。</param>
/// <param name="proxy">経路の記録に使う。null なら <see cref="HttpClient.DefaultProxy"/>。</param>
public sealed class NetworkLog(Action<string>? sink, IWebProxy? proxy = null)
{
    private int _proxyLogged;

    /// <summary>何も記録しない。</summary>
    public static NetworkLog None { get; } = new(null);

    /// <summary>1行を書く。<c>area: message</c> の形。</summary>
    public void Write(string area, string message)
    {
        if (sink is null) return;

        try
        {
            sink($"{area}: {message}");
        }
        catch (Exception)
        {
            // 記録できなくても、通信は続ける
        }
    }

    /// <summary>
    /// 失敗を1行で残す。
    /// <para>例：<c>Google 同期: 失敗。HttpRequestException: … ← AuthenticationException: … （種類=CertificateProblem）</c></para>
    /// </summary>
    /// <param name="area">場面（「Google 同期」「Google 接続」「実働日の配信」など）。</param>
    /// <param name="ex">失敗。</param>
    /// <param name="kind">分けた種類。</param>
    /// <param name="what">何をしていたか。省略すると「失敗」だけ。</param>
    public void Failure(string area, Exception ex, NetworkFailureKind kind, string? what = null)
    {
        ArgumentNullException.ThrowIfNull(ex);

        Write(area, $"{(what is null ? string.Empty : what + "で")}失敗。{NetworkDiagnostics.Summarize(ex)}（種類={kind}）");
    }

    /// <summary>
    /// 通信がどの経路を通るかを記録する（プロセスで1回だけ）。
    /// <para>
    /// 会社の回線は、Windows の設定や PAC でプロキシ経由になっていることが多い。経路を調べる処理は、
    /// 自動設定の取得で数秒止まることがあるので、画面のスレッドでは呼ばないこと。
    /// </para>
    /// </summary>
    public void LogProxyOnce(string area, string url)
    {
        if (sink is null) return;
        if (Interlocked.Exchange(ref _proxyLogged, 1) != 0) return;

        try
        {
            Write(area, NetworkDiagnostics.DescribeProxy(
                proxy ?? HttpClient.DefaultProxy, new Uri(url), KadoHttp.UsesDefaultProxyCredentials));
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or NotSupportedException
                                       or InvalidOperationException)
        {
            Write(area, $"経路を調べられなかった（{ex.GetType().Name}）");
        }
    }
}
