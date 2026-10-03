using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json;
using System.Xml;
using Kado.Core.Net;

namespace Kado.Presentation.Net;

/// <summary>通信が失敗した理由の種類。利用者に伝える文言を分けるためのもの。更新・Google・実働日の配信で共通。</summary>
public enum NetworkFailureKind
{
    /// <summary>上に当てはまらない。</summary>
    Other,

    /// <summary>
    /// 429（呼びすぎ）。更新の確認では 403 もこれにする。GitHub の API の未認証の上限
    /// （1 時間 60 回、同じ出口の IP で共有）に当たったときに出る。
    /// </summary>
    RateLimited,

    /// <summary>407。プロキシが認証を求めている。</summary>
    ProxyAuthRequired,

    /// <summary>プロキシ経由の接続（CONNECT）を確立できなかった。407 以外。</summary>
    ProxyError,

    /// <summary>時間内に応答が無かった。</summary>
    Timeout,

    /// <summary>接続できない（名前が引けない、拒否された、届かない）。</summary>
    CannotConnect,

    /// <summary>TLS の証明書を確かめられなかった。社内の通信検査が入っているときに出る。</summary>
    CertificateProblem,

    /// <summary>404。</summary>
    NotFound,

    /// <summary>5xx。</summary>
    ServerError,

    /// <summary>応答を読み取れなかった（壊れた XML/JSON、想定外の形）。</summary>
    UnreadableResponse,

    /// <summary>取りに行ったのに、Web ページ（HTML）などが返った。中継のエラーページなど。</summary>
    WebPageInsteadOfFile,

    /// <summary>
    /// 403。相手（Google）が断ってきた。権限が無い、API が有効でない、など。
    /// 更新の確認では GitHub の上限と見分けがつかないので <see cref="RateLimited"/> にする。
    /// </summary>
    Forbidden,

    /// <summary>401。アクセストークンが通らなかった。</summary>
    Unauthorized,
}

/// <summary>
/// 取りに行ったものではなく、別のもの（Web ページ、実行ファイルの形をしていないもの）が返った。
/// <para>
/// 会社のプロキシは、止めた通信の代わりに 200 でエラーページを返すことがある。
/// 落としたものをそのまま実行するので、これを exe として扱ってはいけない。
/// </para>
/// </summary>
public sealed class UnexpectedContentException(string contentType)
    : Exception($"取りに行ったものではないものが返りました（Content-Type={contentType}）。")
{
    public string ContentType { get; } = contentType;
}

/// <summary>
/// 通信の失敗を種類に分ける。更新・Google・実働日の配信で同じ分け方を使う。
/// <para>
/// ネットワークの失敗は、本当の理由が内側の例外に入る。外側の <c>HttpRequestException</c>
/// だけでは「通信できません」としか分からないので、連鎖をすべてたどって決める。
/// </para>
/// </summary>
public static class NetworkFailure
{
    /// <summary>
    /// 例外の連鎖（内側の例外まで）から、失敗の種類を決める。
    /// <para>
    /// ネットワークの失敗は、本当の理由が内側の例外に入る。外側の <c>HttpRequestException</c>
    /// だけでは「通信できません」としか分からない。
    /// </para>
    /// </summary>
    public static NetworkFailureKind Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var chain = Flatten(ex).ToList();

        // 状態コードがあれば、それがいちばん確かな手掛かり
        foreach (var http in chain.OfType<HttpRequestException>())
        {
            if (http.StatusCode is { } code && ClassifyStatus(code) is { } byStatus) return byStatus;
        }

        if (chain.OfType<UnexpectedContentException>().Any()) return NetworkFailureKind.WebPageInsteadOfFile;

        if (chain.OfType<HttpRequestException>().Any(h => h.HttpRequestError == HttpRequestError.ProxyTunnelError))
        {
            return NetworkFailureKind.ProxyError;
        }

        if (chain.OfType<AuthenticationException>().Any() ||
            chain.OfType<HttpRequestException>().Any(h => h.HttpRequestError == HttpRequestError.SecureConnectionError))
        {
            return NetworkFailureKind.CertificateProblem;
        }

        if (chain.OfType<TimeoutException>().Any() || chain.OfType<OperationCanceledException>().Any())
        {
            return NetworkFailureKind.Timeout;
        }

        if (chain.OfType<System.Net.Sockets.SocketException>().Any() ||
            chain.OfType<HttpRequestException>().Any(h =>
                h.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError))
        {
            return NetworkFailureKind.CannotConnect;
        }

        if (chain.Any(e => e is JsonException or XmlException or InvalidDataException))
        {
            return NetworkFailureKind.UnreadableResponse;
        }

        return NetworkFailureKind.Other;
    }

    /// <summary>
    /// 状態コードから種類を決める。決められないものは null。
    /// <para>403 は GitHub の API の上限としても出るので、ここでは <see cref="NetworkFailureKind.RateLimited"/> にする。</para>
    /// </summary>
    public static NetworkFailureKind? ClassifyStatus(HttpStatusCode status) => (int)status switch
    {
        403 or 429 => NetworkFailureKind.RateLimited,
        407 => NetworkFailureKind.ProxyAuthRequired,
        404 => NetworkFailureKind.NotFound,
        >= 500 and <= 599 => NetworkFailureKind.ServerError,
        _ => null,
    };

    /// <summary>例外と、その内側の例外をすべて並べる（<see cref="AggregateException"/> も展開する）。</summary>
    internal static IEnumerable<Exception> Flatten(Exception ex, int depth = 0)
    {
        if (depth >= NetworkDiagnostics.MaxExceptionDepth) yield break;

        yield return ex;

        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                foreach (var e in Flatten(inner, depth + 1)) yield return e;
            }

            yield break;
        }

        if (ex.InnerException is { } next)
        {
            foreach (var e in Flatten(next, depth + 1)) yield return e;
        }
    }
}
